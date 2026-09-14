using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Avatares em cache de memoria e de disco.
///
/// A URL do CDN embute o hash do avatar, entao ela muda sozinha quando a pessoa
/// troca de foto - nao ha invalidacao a fazer, arquivos antigos so ficam orfaos e
/// sao limpos por <see cref="PruneDisk"/>.
/// </summary>
public static class AvatarCache
{
    /// <summary>
    /// Maior tamanho em que um avatar e desenhado: 30 DIP (grande) a 150% de escala.
    /// Decodificar ja nesse tamanho gasta ~9 KB por avatar em vez de 16 KB, e a
    /// reducao feita pelo decodificador fica mais nitida que a do WPF na hora de desenhar.
    /// </summary>
    private const int DecodePixels = 48;

    /// <summary>
    /// Limite de avatares em memoria. O app fica aberto dias; sem teto, cada pessoa
    /// que ja passou por uma call ficaria residente para sempre.
    /// </summary>
    private const int MaxInMemory = 64;

    private static readonly TimeSpan DiskRetention = TimeSpan.FromDays(30);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, BitmapImage> Memory = new();

    /// <summary>Acerto de cache sem trocar de thread, para o caso comum.</summary>
    public static bool TryGetCached(string url, out BitmapImage image) => Memory.TryGetValue(url, out image!);

    public static async Task<BitmapImage?> GetAsync(string url)
    {
        if (Memory.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bytes = await ReadOrDownloadAsync(url);
            var image = Decode(bytes);

            if (Memory.Count >= MaxInMemory) Memory.Clear();
            Memory[url] = image;

            return image;
        }
        catch
        {
            // Sem avatar a UI cai no circulo com a inicial do nome.
            return null;
        }
    }

    /// <summary>Remove do disco avatares que nao sao usados ha mais de 30 dias.</summary>
    public static void PruneDisk()
    {
        try
        {
            if (!Directory.Exists(AppPaths.AvatarCache)) return;

            var cutoff = DateTime.UtcNow - DiskRetention;
            foreach (var file in Directory.EnumerateFiles(AppPaths.AvatarCache, "*.png"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Limpeza e oportunista; tenta de novo na proxima inicializacao.
        }
    }

    private static async Task<byte[]> ReadOrDownloadAsync(string url)
    {
        var path = Path.Combine(AppPaths.AvatarCache, FileNameFor(url));

        if (File.Exists(path))
        {
            // O NTFS costuma nao atualizar a data de acesso, entao a data de escrita
            // serve de "ultimo uso" para a limpeza. So regrava quando ja esta velha, para
            // nao escrever no disco a cada leitura.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }

            return await File.ReadAllBytesAsync(path);
        }

        var bytes = await Http.GetByteArrayAsync(url);

        Directory.CreateDirectory(AppPaths.AvatarCache);
        await File.WriteAllBytesAsync(path, bytes);

        return bytes;
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = DecodePixels;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();

        // Freeze: torna o bitmap seguro para usar na thread de UI tendo sido
        // decodificado em outra.
        image.Freeze();

        return image;
    }

    private static string FileNameFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hash)[..32] + ".png";
    }
}
