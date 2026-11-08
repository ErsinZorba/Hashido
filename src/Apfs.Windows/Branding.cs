namespace Apfs.Windows;

/// <summary>
/// Ürünün kullanıcıya görünen adı: tek kaynak. Pencere başlıkları, tepsi, ileti kutuları ve kurulum klasörü buradan okur;
/// metin kaynaklarında ad "{0}" yer tutucusuyla geçer. <c>Directory.Build.props</c> (Product) ve
/// <c>installer/APFS-for-Windows.iss</c> (AppName) aynı değeri taşımalı (BrandingTests denetler).
/// Teknik kimlikler bu adla DEĞİŞMEZ: hizmet adı APFSforWindows, kanal, mutex, <c>HKCU\Software\APFS for Windows</c>,
/// ProgramData günlük klasörü, Inno AppId, HKLM Run değer adı (hepsi 1.3.0 ve öncesinin adı "APFS for Windows"la kalır).
/// </summary>
public static class Branding
{
    public const string ProductName = "Hashido";
}
