using Octopus.Remote.Security;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Octopus.Remote.Tests;

/// <summary>
/// Round-trip verification of our pure managed ISO/IEC 18004 <see cref="QrCodeEncoder"/> against an independent
/// standard decoder (<see cref="QRCodeReader"/>) across short, realistic pairing/relay URIs, UTF-8 names, and large
/// multi-block versions (>= 7).
/// </summary>
public class QrCodeTests
{
    [Theory]
    [InlineData("hello", QrErrorCorrectionLevel.Low)]
    [InlineData("hello", QrErrorCorrectionLevel.Medium)]
    [InlineData("hello", QrErrorCorrectionLevel.Quartile)]
    [InlineData("hello", QrErrorCorrectionLevel.High)]
    [InlineData("octopus://relay?v=1&url=https%3A%2F%2Frelay.example.com&key=super-secret-key-123456", QrErrorCorrectionLevel.Medium)]
    [InlineData("octopus://pair?v=1&id=ABCDEFGHJKMNPQRSTUVWXYZ234&fp=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&t=BBBBBBBBBBBBBBBBBBBBBA&n=%E5%B7%A5%E4%BD%9C%E7%AB%99%20MacBook&a=ws%3A%2F%2F192.168.1.20%3A47600", QrErrorCorrectionLevel.Medium)]
    public void Encoded_qr_codes_decode_identically_with_standard_reader(string payload, QrErrorCorrectionLevel ecc)
    {
        var matrix = QrCodeEncoder.Encode(payload, ecc);
        Assert.Equal(17 + 4 * matrix.Version, matrix.Size);
        Assert.Equal(payload, Decode(matrix));
    }

    [Fact]
    public void Real_pairing_link_with_multiple_addresses_round_trips_through_qr()
    {
        var host = DeviceIdentity.Create();
        var token = Enumerable.Range(1, 16).Select(i => (byte)(i * 11)).ToArray();
        var link = new PairingLink(
            host.Id,
            host.PublicKey.Fingerprint(),
            token,
            "Ada 的 Studio",
            ["ws://192.168.1.20:47600", "ws://10.0.0.12:47600", $"octopus-relay://{host.Id}"]);

        var encoded = link.ToString();
        var matrix = QrCodeEncoder.Encode(encoded, QrErrorCorrectionLevel.Medium);
        Assert.InRange(matrix.Version, 7, 16);

        var decoded = Decode(matrix);
        Assert.Equal(encoded, decoded);
        var parsed = PairingLink.TryParse(decoded);
        Assert.NotNull(parsed);
        Assert.Equal(host.Id, parsed!.DeviceId);
        Assert.Equal(token, parsed.Token);
        Assert.Equal("Ada 的 Studio", parsed.Name);
        Assert.Equal(3, parsed.Addresses.Count);
    }

    [Fact]
    public void Large_payload_uses_multi_block_version_info_and_decodes()
    {
        var payload = new string('A', 600) + "-中文-" + new string('Z', 200);
        var matrix = QrCodeEncoder.Encode(payload, QrErrorCorrectionLevel.Quartile);
        Assert.True(matrix.Version >= 15);
        Assert.Equal(payload, Decode(matrix));
    }

    private static string Decode(QrCodeMatrix matrix)
    {
        const int quiet = 4;
        const int scale = 4;
        var totalModules = matrix.Size + quiet * 2;
        var side = totalModules * scale;
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);

        for (var y = 0; y < matrix.Size; y++)
        {
            for (var x = 0; x < matrix.Size; x++)
            {
                if (!matrix[x, y]) continue;
                var py0 = (y + quiet) * scale;
                var px0 = (x + quiet) * scale;
                for (var dy = 0; dy < scale; dy++)
                {
                    var row = (py0 + dy) * side + px0;
                    for (var dx = 0; dx < scale; dx++) luminance[row + dx] = 0;
                }
            }
        }

        var source = new PlanarYUVLuminanceSource(luminance, side, side, 0, 0, side, side, false);
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        var reader = new QRCodeReader();
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var result = reader.decode(bitmap, hints);
        Assert.NotNull(result);
        return result!.Text;
    }
}
