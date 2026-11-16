using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Apfs.Mount;

/// <summary>Elige una letra libre antes de pedirle a WinFsp que monte el sistema de archivos.</summary>
internal static class DriveLetterAllocator
{
    // El servicio LocalSystem no ve necesariamente las unidades de red de la sesión interactiva; evita el tramo U:–Z:,
    // donde suelen estar esas asignaciones, siguiendo el criterio ya usado por Apfs.DevWriter.
    private const string CandidateLetters = "TSRQPONMLKJIHGF";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    public static string GetFreeMountPoint()
    {
        // El mapa contiene las unidades visibles para el proceso; se combina con el tramo conservador para no depender
        // de que los mapeos de red de otro inicio de sesión sean visibles para el servicio.
        uint occupied = GetLogicalDrives();
        if (occupied == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new ApfsException(ApfsErrorCode.IoError,
                CoreText.T("Fs.DriveEnumerationFailed", error, new Win32Exception(error).Message));
        }
        return FindFreeMountPoint(occupied);
    }

    internal static string FindFreeMountPoint(uint occupied)
    {
        foreach (char letter in CandidateLetters)
            if ((occupied & (1u << (letter - 'A'))) == 0)
                return $"{letter}:";

        throw new ApfsException(ApfsErrorCode.IoError, CoreText.T("Fs.NoFreeDriveLetter"));
    }
}
