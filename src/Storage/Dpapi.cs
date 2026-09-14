using System.Runtime.InteropServices;
using System.Text;

namespace SshManager.Storage;

public static class Dpapi
{
    // Flag usada pelo DPAPI para suprimir prompts de UI
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string Protect( string plain )
    {
        var bytes = Encoding.UTF8.GetBytes( plain );
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob.Init(bytes);
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new InvalidOperationException("CryptProtectData failed");
            return Convert.ToBase64String(outBlob.ToBytes());
        }
        finally { inBlob.Free(); outBlob.FreeLocal(); }
    }

    public static string Unprotect(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob.Init(bytes);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new InvalidOperationException("CryptUnProtectData failed");
            return Encoding.UTF8.GetString(outBlob.ToBytes());
        }
        finally { inBlob.Free(); outBlob.FreeLocal(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;

        public void Init(byte[] data)
        {
            cbData = data.Length;
            pbData = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pbData, data.Length);
        }

        public byte[] ToBytes()
        {
            var b = new byte[cbData];
            Marshal.Copy(pbData, b, 0, cbData);
            return b;
        }

        public void Free()
        {
            if(pbData != IntPtr.Zero ) { Marshal.FreeHGlobal(pbData); pbData = IntPtr.Zero; }
        }

        public void FreeLocal()
        {
            if (pbData != IntPtr.Zero) { LocalFree(pbData); pbData = IntPtr.Zero;}
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}