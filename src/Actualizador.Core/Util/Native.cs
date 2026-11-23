using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Actualizador.Core
{
    public static class Native
    {
        // ---------------------------------------------------------------- Versión real del SO
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll")]
        public static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

        // ---------------------------------------------------------------- Caché del cliente DNS
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        static extern int DnsFlushResolverCacheNative();

        /// <summary>
        /// Vacía la caché del cliente DNS de Windows (lo mismo que «ipconfig /flushdns»). Mientras la red está
        /// caída, Windows guarda «el nombre no existe» por unos minutos y lo sigue contestando cuando vuelve.
        /// false si no se pudo (fuera de Windows, o sin el servicio Cliente DNS).
        /// </summary>
        public static bool FlushDnsCache()
        {
            try { return DnsFlushResolverCacheNative() != 0; }
            catch (Exception ex) { Log.Quiet("Vaciar la caché DNS", ex); return false; }
        }

        [DllImport("kernel32.dll")]
        public static extern int GetOEMCP();

        [DllImport("kernel32.dll")]
        public static extern int GetACP();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

        /// <summary>Resuelve cadenas "@archivo.dll,-123" (nombres de reglas de firewall, apps, etc.).</summary>
        public static string ResolveIndirect(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '@') return s;
            try
            {
                var sb = new StringBuilder(1024);
                return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s;
            }
            catch { return s; }
        }

        // 2.0.11: se sacaron las consultas de la época del auditor que ya no se usaban (cuentas y grupos locales, política de
        // contraseñas, perfiles Wi-Fi y puertos abiertos). Un programa que sólo actualiza no las necesita, y juntas son el
        // perfil que la protección de identidad de los antivirus (Avast: IDP.Generic) asocia con los ladrones de datos.

        // ---------------------------------------------------------------- Authenticode (wintrust)
        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        // Layout de Windows 7 (sin pSignatureSettings); Windows 8+ acepta el tamaño menor.
        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
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
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr hFindFile);

        public const uint IO_REPARSE_TAG_APPEXECLINK = 0x8000001B;

        /// <summary>Etiqueta del punto de reanálisis del archivo (0 si no lo es o no se pudo leer).</summary>
        public static uint ReparseTag(string path)
        {
            WIN32_FIND_DATAW d;
            IntPtr h = FindFirstFileW(path, out d);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return 0;
            FindClose(h);
            return (d.dwFileAttributes & 0x400) != 0 ? d.dwReserved0 : 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
                                         uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
                                           byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Las cadenas de un alias de ejecución de apps (APPEXECLINK): familia del paquete, AUMID y
        /// ejecutable de destino, en ese orden. null si no es un alias o no se pudo leer.
        /// </summary>
        public static string[] AppExecLinkStrings(string path)
        {
            const uint FILE_READ_ATTRIBUTES = 0x80, OPEN_EXISTING = 3;
            const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
            IntPtr h = CreateFileW(path, FILE_READ_ATTRIBUTES, 7, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            try
            {
                var buf = new byte[16 * 1024];
                uint got;
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buf, (uint)buf.Length, out got, IntPtr.Zero)) return null;
                return ParseAppExecLink(buf, (int)got);
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// Interpreta REPARSE_DATA_BUFFER de un APPEXECLINK: etiqueta (4 bytes), largo (2), reservado (2),
        /// versión (4) y después cadenas UTF-16 terminadas en cero. null si no es un alias válido.
        /// </summary>
        public static string[] ParseAppExecLink(byte[] buf, int length)
        {
            if (buf == null || length < 16 || length > buf.Length) return null;
            if (BitConverter.ToUInt32(buf, 0) != IO_REPARSE_TAG_APPEXECLINK) return null;
            int dataLength = BitConverter.ToUInt16(buf, 4);
            int end = Math.Min(length, 8 + dataLength);
            var list = new System.Collections.Generic.List<string>();
            int start = 12;
            for (int i = 12; i + 1 < end; i += 2)
            {
                if (buf[i] != 0 || buf[i + 1] != 0) continue;
                list.Add(System.Text.Encoding.Unicode.GetString(buf, start, i - start));
                start = i + 2;
            }
            return list.Count >= 3 ? list.ToArray() : null;
        }

        public const int CERT_E_REVOKED = unchecked((int)0x800B010C);
        /// <summary>El archivo no tiene ninguna firma Authenticode (2.0.12: 7-Zip no firma sus instaladores).</summary>
        public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

        // ---------------------------------------------------------------- Controladores (newdev)
        /// <summary>Instala el controlador aunque Windows considere mejor el que tiene (el usuario lo pidió).</summary>
        public const uint INSTALLFLAG_FORCE = 0x00000001;

        /// <summary>
        /// Instala el controlador de un INF en los dispositivos que tienen ese identificador (como el
        /// Administrador de dispositivos con «Usar disco»). Devuelve false y deja el error en GetLastError.
        /// </summary>
        [DllImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags,
                                                                   [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
        public const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
        public const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
        public const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);

        /// <summary>
        /// Firma de un instalador descargado, con control de revocación de toda la cadena: un
        /// certificado robado y revocado no pasa. Si no se puede consultar la revocación (sin red,
        /// proxy), se acepta la firma válida sin ese control y se informa en <paramref name="note"/>.
        /// </summary>
        public static int VerifyAuthenticodeStrict(string path, out string note)
        {
            note = null;
            int rc = VerifyAuthenticode(path, 1 /* WTD_REVOKE_WHOLECHAIN */);
            if (rc == CERT_E_REVOCATION_FAILURE || rc == CRYPT_E_REVOCATION_OFFLINE || rc == CRYPT_E_NO_REVOCATION_CHECK)
            {
                note = "no se pudo consultar la revocación del certificado (" + Fmt.Hex(rc) + "); firma verificada sin ese control";
                return VerifyAuthenticode(path, 0);
            }
            return rc;
        }

        static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
        public const int CERT_E_CHAINING = unchecked((int)0x800B010A);

        /// <summary>
        /// Devuelve 0 si la firma Authenticode del archivo es válida y de confianza. Sin consultar la red; si la cadena
        /// no se pudo armar sólo con lo que hay en el equipo (raíz o intermedio que Windows todavía no descargó), se
        /// repite UNA vez dejando que Windows los busque, sin control de revocación. Antes, en un equipo recién
        /// instalado, firmas válidas (las de Azure Trusted Signing, DigiCert G4…) figuraban como inválidas.
        /// </summary>
        /// <summary>
        /// Sujeto del certificado que firma el archivo (Authenticode). 2.0.8: el certificado se libera; antes cada archivo verificado
        /// dejaba un identificador de certificado abierto hasta que pasara el recolector de basura.
        /// </summary>
        public static string SignerSubject(string path)
        {
            using (var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path)) return cert.Subject;
        }

        public static int VerifyAuthenticode(string path)
        {
            int rc = VerifyAuthenticode(path, 0);
            if (!NeedsChainDownload(rc)) return rc;
            lock (chainGate) if (DateTime.UtcNow < chainRetryBlockedUntil) return rc;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int again = VerifyAuthenticodeChain(path);
            // Sin acceso a la red (filtro DNS, sin conexión) cada intento espera el plazo de Windows: si tardó y no sirvió,
            // durante 10 minutos no se vuelve a intentar, para no demorar la auditoría archivo por archivo.
            if (NeedsChainDownload(again) && sw.ElapsedMilliseconds > 3000)
                lock (chainGate) chainRetryBlockedUntil = DateTime.UtcNow.AddMinutes(10);
            return again;
        }

        static readonly object chainGate = new object();
        static DateTime chainRetryBlockedUntil = DateTime.MinValue;

        /// <summary>¿El error se debe a que falta un certificado de la cadena en el equipo (y Windows lo puede descargar)?</summary>
        public static bool NeedsChainDownload(int rc)
        {
            return rc == CERT_E_UNTRUSTEDROOT || rc == CERT_E_CHAINING;
        }

        /// <summary>
        /// Firma válida y de confianza, armando la cadena con la red si hace falta (raíz o intermedio que falten), sin
        /// control de revocación. Para saber de quién es un programa ya instalado antes de aceptar su actualización.
        /// </summary>
        public static int VerifyAuthenticodeChain(string path)
        {
            return VerifyAuthenticode(path, 2);
        }

        /// <param name="revocation">0 = sin revocación ni red; 1 = revocación de toda la cadena (con red); 2 = sin revocación, con red para armar la cadena.</param>
        static int VerifyAuthenticode(string path, uint revocation)
        {
            var fi = new WINTRUST_FILE_INFO();
            fi.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
            fi.pcwszFilePath = path;
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var wd = new WINTRUST_DATA();
                wd.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                wd.dwUIChoice = 2;           // WTD_UI_NONE
                wd.fdwRevocationChecks = revocation == 1 ? 1u : 0u;  // 0 = WTD_REVOKE_NONE, 1 = WTD_REVOKE_WHOLECHAIN
                wd.dwUnionChoice = 1;        // WTD_CHOICE_FILE
                wd.pFile = pFile;
                wd.dwStateAction = 0;        // WTD_STATEACTION_IGNORE
                // Con control de revocación: WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT (consulta CRL/OCSP).
                // Sin control: WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL, sin red. Antes
                // la verificación «sin control» también consultaba la red, por la bandera de la cadena:
                // cada ejecutable de inicio podía esperar la CRL, y sin acceso a ella (un filtro DNS)
                // hasta la verificación de respaldo fallaba.
                // Modo 2: WTD_REVOCATION_CHECK_NONE sin WTD_CACHE_ONLY_URL_RETRIEVAL (puede descargar la raíz o un intermedio).
                wd.dwProvFlags = revocation == 1 ? 0x00000080u : revocation == 2 ? 0x00000010u : (0x00000010u | 0x00001000u);
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(wd, pData, false);
                return WinVerifyTrust(new IntPtr(-1), WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }
        }
    
        // ---------------------------------------------------------------- Paquetes MSI (2.0.13)
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out IntPtr phDatabase);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint MsiDatabaseOpenViewW(IntPtr hDatabase, string szQuery, out IntPtr phView);
        [DllImport("msi.dll", ExactSpelling = true)]
        static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);
        [DllImport("msi.dll", ExactSpelling = true)]
        static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint MsiRecordGetStringW(IntPtr hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);
        [DllImport("msi.dll", ExactSpelling = true)]
        static extern uint MsiCloseHandle(IntPtr hAny);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint MsiEnumRelatedProductsW(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

        /// <summary>
        /// Propiedades de un paquete .msi (tabla Property, sólo lectura, sin ejecutar nada): ProductName, ProductVersion, Manufacturer,
        /// UpgradeCode, ProductCode. Las que no tiene no aparecen. Vacío si no se puede abrir.
        /// </summary>
        public static Dictionary<string, string> MsiProperties(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return d;
            IntPtr db = IntPtr.Zero, view = IntPtr.Zero;
            try
            {
                if (MsiOpenDatabaseW(path, IntPtr.Zero, out db) != 0) return d;   // MSIDBOPEN_READONLY
                if (MsiDatabaseOpenViewW(db, "SELECT `Property`, `Value` FROM `Property`", out view) != 0) return d;
                if (MsiViewExecute(view, IntPtr.Zero) != 0) return d;
                for (int n = 0; n < 2000; n++)
                {
                    IntPtr rec;
                    if (MsiViewFetch(view, out rec) != 0) break;
                    try
                    {
                        string name = RecordString(rec, 1), value = RecordString(rec, 2);
                        if (name.Length > 0 && (name == "ProductName" || name == "ProductVersion" || name == "Manufacturer" || name == "UpgradeCode" || name == "ProductCode"))
                            d[name] = value;
                    }
                    finally { MsiCloseHandle(rec); }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally
            {
                if (view != IntPtr.Zero) MsiCloseHandle(view);
                if (db != IntPtr.Zero) MsiCloseHandle(db);
            }
            return d;
        }

        static string RecordString(IntPtr rec, uint field)
        {
            uint size = 512;
            var sb = new StringBuilder((int)size);
            uint r = MsiRecordGetStringW(rec, field, sb, ref size);
            if (r == 234) { size++; sb = new StringBuilder((int)size); r = MsiRecordGetStringW(rec, field, sb, ref size); }   // ERROR_MORE_DATA
            return r == 0 ? sb.ToString() : "";
        }

        /// <summary>Los ProductCode instalados en el equipo que comparten ese UpgradeCode (la «familia» del producto en Windows Installer).</summary>
        public static List<string> MsiRelatedProducts(string upgradeCode)
        {
            var list = new List<string>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || string.IsNullOrEmpty(upgradeCode)) return list;
            try
            {
                for (uint i = 0; i < 64; i++)
                {
                    var sb = new StringBuilder(39);
                    if (MsiEnumRelatedProductsW(upgradeCode, 0, i, sb) != 0) break;
                    list.Add(sb.ToString());
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return list;
        }
}
}
