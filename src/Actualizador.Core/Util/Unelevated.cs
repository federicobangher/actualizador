// Ejecutar un programa SIN privilegios de administrador desde Actualizador elevado.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Actualizador.Core
{
    /// <summary>
    /// Ejecuta un programa como el usuario de la sesión, sin elevar, aunque Actualizador corra como administrador. Se le
    /// pide al Explorador de Windows que lo abra (IShellDispatch2.ShellExecute del escritorio), la forma que recomienda
    /// Microsoft: no se duplican tokens ni se tocan privilegios (lo que vigilan los antivirus). Después se busca el proceso
    /// que abrió el Explorador para esperar a que termine y leer su código de salida. No hay salida de texto: la ventana
    /// va oculta (o visible con «interactiva»).
    ///
    /// Para qué: hay instaladores que se niegan a correr como administrador (Spotify y otras apps que se instalan sólo
    /// para el usuario; winget responde 0x8A150056). Con Actualizador elevado —lo normal para un administrador— esos
    /// programas no se actualizaban nunca.
    /// </summary>
    public static class Unelevated
    {
        /// <summary>Sólo para las pruebas: reemplaza la ejecución (archivo, argumentos → resultado).</summary>
        public static Func<string, string, ProcResult> RunForTests;

        /// <summary>Cuánto se busca el proceso que abrió el Explorador.</summary>
        public static int FindProcessMs = 20000;

        /// <summary>
        /// Ejecuta y espera. <see cref="ProcResult.Started"/> false (con el motivo) si no se pudo pedir al Explorador.
        /// Si arrancó pero no se encontró el proceso para leer su código, ExitCode queda en <see cref="UnknownExit"/>.
        /// </summary>
        public static ProcResult Run(string file, string args, int timeoutMs, bool visible)
        {
            if (RunForTests != null) return RunForTests(file, args) ?? new ProcResult();
            var r = new ProcResult();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { r.StartError = "sólo en Windows"; return r; }
            string exeName = Path.GetFileName(file ?? "");
            var before = new HashSet<int>();
            foreach (var e in ProcessList.Snapshot(false))
                if (string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase)) before.Add(e.Pid);
            DateTime t0 = DateTime.Now.AddSeconds(-2);
            int shellPid = ShellPid();
            try { ShellExecuteAsDesktop(file, args ?? "", Environment.GetFolderPath(Environment.SpecialFolder.System), visible); }
            catch (Exception ex)
            {
                r.StartError = "el Explorador de Windows no pudo abrirlo sin elevar (" + ex.GetType().Name + ": " + ex.Message + ")";
                return r;
            }
            r.Started = true;
            Process p = FindNew(exeName, before, shellPid, t0);
            if (p == null)
            {
                // Terminó antes de encontrarlo, o no se lo ve: el resultado se confirma con la versión instalada.
                Log.Warn(exeName + " sin elevar: no se encontró el proceso para esperar su código de salida.");
                r.ExitCode = UnknownExit;
                return r;
            }
            using (p)
            {
                try
                {
                    if (!p.WaitForExit(timeoutMs)) { r.TimedOut = true; try { p.Kill(); } catch { } return r; }
                    r.ExitCode = p.ExitCode;
                }
                catch (Exception ex)
                {
                    Log.Warn(exeName + " sin elevar: no se pudo leer su código de salida (" + ex.Message + ").");
                    r.ExitCode = UnknownExit;
                }
            }
            return r;
        }

        /// <summary>Código de salida desconocido (no se pudo leer); el llamador confirma por otro medio.</summary>
        public const int UnknownExit = int.MinValue + 1;

        static Process FindNew(string exeName, HashSet<int> before, int shellPid, DateTime t0)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < FindProcessMs)
            {
                // Sólo nombres y padres (lo más barato); los detalles, del candidato.
                foreach (var e in ProcessList.Snapshot(false))
                {
                    if (!string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase) || before.Contains(e.Pid)) continue;
                    // Primero sólo los hijos del Explorador del escritorio; pasados unos segundos, cualquiera nuevo (por si
                    // lo creó otro proceso del Explorador).
                    if (shellPid > 0 && e.ParentPid != shellPid && sw.ElapsedMilliseconds < 5000) continue;
                    Process p = null;
                    try
                    {
                        p = Process.GetProcessById(e.Pid);
                        IntPtr h = p.Handle;          // se abre ya: si termina enseguida, el código igual se puede leer
                        bool reciente = true;
                        try { reciente = p.StartTime >= t0; } catch { }
                        if (h == IntPtr.Zero || !reciente) { p.Dispose(); before.Add(e.Pid); continue; }
                        return p;
                    }
                    catch (Exception qx) { if (p != null) p.Dispose(); Log.Quiet("Proceso abierto sin elevar", qx); }
                }
                System.Threading.Thread.Sleep(150);
            }
            return null;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>PID del Explorador que dibuja el escritorio (0 si no hay).</summary>
        static int ShellPid()
        {
            try
            {
                IntPtr w = GetShellWindow();
                if (w == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(w, out pid);
                return unchecked((int)pid);
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- COM del Explorador
        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IServiceProvider
        {
            void QueryService([In] ref Guid guidService, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
        }

        // IShellBrowser (hereda de IOleWindow). Sólo se llama QueryActiveShellView: los demás métodos están para que
        // la tabla de métodos tenga el orden correcto.
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(int fEnterMode);
            void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            void RemoveMenusSB(IntPtr hmenuShared);
            void SetStatusTextSB(IntPtr pszStatusText);
            void EnableModelessSB(int fEnable);
            void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            void BrowseObject(IntPtr pidl, uint wFlags);
            void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
            void GetControlWindow(uint id, out IntPtr phwnd);
            void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
            void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView ppshv);
            void OnViewWindowActive(IntPtr pshv);
            void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
        }

        // IShellView (hereda de IOleWindow). Sólo se llama GetItemObject.
        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellView
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(int fEnterMode);
            void TranslateAccelerator(IntPtr pmsg);
            void EnableModeless(int fEnable);
            void UIActivate(uint uState);
            void Refresh();
            void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
            void DestroyViewWindow();
            void GetCurrentInfo(IntPtr pfs);
            void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
            void SaveViewState();
            void SelectItem(IntPtr pidlItem, uint uFlags);
            void GetItemObject(uint uItem, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }

        // IShellWindows (dual). Sólo se llama FindWindowSW.
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register([In, MarshalAs(UnmanagedType.IDispatch)] object pid, [In] int hwnd, [In] int swClass, out int plCookie);
            void RegisterPending([In] int lThreadId, [In] ref object pvarloc, [In] ref object pvarlocRoot, [In] int swClass, out int plCookie);
            void Revoke([In] int lCookie);
            void OnNavigate([In] int lCookie, [In] ref object pvarLoc);
            void OnActivated([In] int lCookie, [In] bool fActive);
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In] ref object pvarLoc, [In] ref object pvarLocRoot, [In] int swClass, out int pHWND, [In] int swfwOptions);
        }

        static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        static readonly Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");
        const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0;

        /// <summary>
        /// Le pide al escritorio del Explorador que abra el programa (como un doble clic del usuario): corre con el token
        /// de la sesión, sin elevar.
        /// </summary>
        static void ShellExecuteAsDesktop(string file, string args, string dir, bool visible)
        {
            Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows, true);
            object windows = Activator.CreateInstance(t);
            object desktopDispatch = null, browserObj = null, dispView = null, app = null;
            IShellView view = null;
            try
            {
                var sw = (IShellWindows)windows;
                object loc = CSIDL_DESKTOP, empty = null;
                int hwnd;
                desktopDispatch = sw.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
                if (desktopDispatch == null) throw new InvalidOperationException("no hay escritorio del Explorador");
                var sp = (IServiceProvider)desktopDispatch;
                Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
                sp.QueryService(ref sid, ref iid, out browserObj);
                var browser = (IShellBrowser)browserObj;
                browser.QueryActiveShellView(out view);
                Guid idisp = IID_IDispatch;
                view.GetItemObject(SVGIO_BACKGROUND, ref idisp, out dispView);
                app = Com.Get(dispView, "Application");                    // IShellFolderViewDual.Application → IShellDispatch2
                Com.Call(app, "ShellExecute", file, args, dir, "open", visible ? 1 : 0);
            }
            finally
            {
                foreach (object o in new object[] { app, dispView, view, browserObj, desktopDispatch, windows })
                    if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }
    }
}
