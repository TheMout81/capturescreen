using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PureClean.Infrastructure;

/// <param name="IsValid">Signature présente, intacte et émise par une autorité reconnue par Windows.</param>
/// <param name="Signer">Sujet du certificat (ex. « CN=Microsoft Corporation, O=Microsoft Corporation… »).</param>
public sealed record SignatureInfo(bool IsValid, string? Signer)
{
    public bool IsSignedBy(string publisher)
        => IsValid && Signer?.Contains(publisher, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Vérifie la signature numérique (Authenticode) d'un exécutable avec WinVerifyTrust.</summary>
public static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    public static SignatureInfo Verify(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = filePointer,
                dwStateAction = StateActionVerify,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return new SignatureInfo(result == 0, result == 0 ? Signer(path) : null);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Lecture du certificat d'un fichier signé : pas d'équivalent non obsolète.
            return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
