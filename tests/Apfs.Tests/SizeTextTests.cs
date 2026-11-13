using System.Globalization;
using Apfs.Windows.Localization;

namespace Apfs.Tests;

/// <summary>Tepsideki doluluk satırının boyut yazımı (Dosya Gezgini gibi: ikili birim, üç anlamlı basamak, kesme).</summary>
public class SizeTextTests
{
    [Theory]
    [InlineData(0L, "0 bayt")]
    [InlineData(-5L, "0 bayt")]
    [InlineData(999L, "999 bayt")]
    [InlineData(1000L, "0,97 KB")]          // 1000'e ulaşan bir üst birime geçer
    [InlineData(1536L, "1,50 KB")]
    [InlineData(1048576000L, "0,97 GB")]    // 1000 MB
    [InlineData(10737418239L, "9,99 GB")]   // 10 GB'tan bir bayt az: yuvarlanmaz, kesilir
    [InlineData(16106127360L, "15,0 GB")]
    [InlineData(164282499072L, "153 GB")]
    [InlineData(500107862016L, "465 GB")]   // "500 GB" satılan disk
    [InlineData(1999000000000L, "1,81 TB")] // "2 TB" satılan disk
    public void FormatsLikeExplorer(long bytes, string expected) => Assert.Equal(expected, SizeText.Format(bytes));

    [Fact]
    public void NumberFormatComesFromTheGivenCulture() =>
        Assert.Equal("1.81 TB", SizeText.Format(1999000000000L, CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void UnitNamesComeFromTheInterfaceLanguage()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            Assert.Equal("1,81 To", SizeText.Format(1999000000000L));
            Assert.Equal("12 octets", SizeText.Format(12));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
