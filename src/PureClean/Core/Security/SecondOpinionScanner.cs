using System.Diagnostics;
using System.Net.Http;
using PureClean.Infrastructure;

namespace PureClean.Core.Security;

/// <summary>
/// Scanner antimalware d'un éditeur reconnu, téléchargé à chaque utilisation pour avoir
/// ses signatures du jour, et lancé seulement si sa signature numérique est celle de l'éditeur.
/// </summary>
/// <param name="ExpectedSigner">Nom attendu dans le certificat de signature.</param>
public sealed record SecondOpinionScanner(
    string Name,
    string Publisher,
    string Description,
    string DownloadUrl,
    string FileName,
    string ExpectedSigner)
{
    public static IReadOnlyList<SecondOpinionScanner> All { get; } =
    [
        new("AdwCleaner", "Malwarebytes",
            "Spécialiste des logiciels publicitaires, barres d'outils et programmes indésirables. Rapide (quelques minutes), sans installation.",
            "https://adwcleaner.malwarebytes.com/adwcleaner?channel=release", "adwcleaner.exe", "Malwarebytes"),
        new("Microsoft Safety Scanner", "Microsoft",
            "Scanner autonome de Microsoft, avec les signatures du jour (valable 10 jours). Utile si Defender a été désactivé ou contourné. Téléchargement d'environ 200 Mo.",
            "https://go.microsoft.com/fwlink/?LinkId=212732", "msert.exe", "Microsoft Corporation"),
        new("Emsisoft Emergency Kit", "Emsisoft",
            "Double moteur d'analyse portable : s'extrait dans un dossier (par défaut C:\\EEK) puis se lance sans installation. Téléchargement volumineux.",
            "https://dl.emsisoft.com/EmsisoftEmergencyKit.exe", "EmsisoftEmergencyKit.exe", "Emsisoft"),
    ];

    private static string Folder => Path.Combine(Path.GetTempPath(), "PureClean", "Scanners");

    /// <summary>Télécharge la dernière version, vérifie la signature de l'éditeur puis lance le scanner.</summary>
    public async Task DownloadAndRunAsync(IProgress<string> progress)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, FileName);
        if (File.Exists(file))
            File.Delete(file);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PureClean");
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(file);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                progress.Report(total is > 0
                    ? $"Téléchargement… {100 * received / total.Value} % ({ByteFormatter.Format(received)})"
                    : $"Téléchargement… {ByteFormatter.Format(received)}");
            }
        }

        progress.Report("Vérification de la signature…");
        var signature = await Task.Run(() => Authenticode.Verify(file));
        if (!signature.IsSignedBy(ExpectedSigner))
        {
            File.Delete(file);
            throw new InvalidOperationException(signature.IsValid
                ? $"le fichier est signé par « {signature.Signer} » et non par {Publisher} : supprimé par sécurité"
                : "signature numérique absente ou invalide : fichier supprimé par sécurité");
        }

        progress.Report("Lancement…");
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Folder });
    }
}
