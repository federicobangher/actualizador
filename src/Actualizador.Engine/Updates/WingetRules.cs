// Reglas de la descarga directa con el manifiesto de winget: elección del instalador, argumentos y códigos de salida.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Qué significa el código de salida del instalador.</summary>
    public sealed class InstallerExit
    {
        public bool Success;
        public bool Reboot;
        /// <summary>No se instaló: el instalador pide reiniciar el equipo antes (ExpectedReturnCodes «rebootRequiredForInstall»).</summary>
        public bool RestartFirst;
        public string Message = "";
    }

    /// <summary>Reglas del repositorio de winget, sin dependencias de Windows (se prueban en Linux).</summary>
    public static class WingetRules
    {
        public const string RawBase = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/";
        public const string ApiBase = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/";

        static readonly Regex SafeVersion = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,63}$", RegexOptions.Compiled);

        public static bool IsSafeVersion(string v)
        {
            return !string.IsNullOrEmpty(v) && SafeVersion.IsMatch(v) && v.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        static bool IsSafePackageId(string id)
        {
            return WingetProvider.IsAsciiId(id) && id.IndexOf('.') > 0 && id.IndexOf("..", StringComparison.Ordinal) < 0 && !id.EndsWith(".");
        }

        /// <summary>
        /// manifests/&lt;inicial&gt;/&lt;Editor&gt;/&lt;Paquete&gt;/&lt;versión&gt;/&lt;Id&gt;.installer.yaml
        /// (o &lt;Id&gt;.yaml en los manifiestos viejos de un solo archivo). null si algo no es seguro.
        /// </summary>
        public static string ManifestUrl(string id, string version, bool singleton)
        {
            if (!IsSafePackageId(id) || !IsSafeVersion(version)) return null;
            return RawBase + char.ToLowerInvariant(id[0]) + "/" + id.Replace('.', '/') + "/" + version + "/" + id + (singleton ? ".yaml" : ".installer.yaml");
        }

        static readonly Regex SafeLocale = new Regex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.Compiled);

        public static bool IsSafeLocale(string locale) { return !string.IsNullOrEmpty(locale) && SafeLocale.IsMatch(locale); }

        /// <summary>
        /// Manifiesto de idioma (…/&lt;Id&gt;.locale.&lt;idioma&gt;.yaml): trae el editor (Publisher) que
        /// revisan los moderadores del repositorio. null si algo no es seguro.
        /// </summary>
        public static string LocaleUrl(string id, string version, string locale)
        {
            if (!IsSafePackageId(id) || !IsSafeVersion(version) || !IsSafeLocale(locale)) return null;
            return RawBase + char.ToLowerInvariant(id[0]) + "/" + id.Replace('.', '/') + "/" + version + "/" + id + ".locale." + locale + ".yaml";
        }

        /// <summary>
        /// Editor del paquete según el repositorio: «Publisher» del manifiesto de un solo archivo, o el
        /// idioma predeterminado («DefaultLocale») del manifiesto de versión, que hay que leer aparte.
        /// </summary>
        public static string PublisherFrom(string versionOrSingleton, out string defaultLocale)
        {
            defaultLocale = "";
            var root = MiniYaml.Parse(versionOrSingleton) as Dictionary<string, object>;
            if (root == null) return "";
            string pub = MiniYaml.Str(root, "Publisher").Trim();
            if (pub.Length > 0) return pub;
            defaultLocale = MiniYaml.Str(root, "DefaultLocale").Trim();
            return "";
        }

        /// <summary>Carpeta del paquete en la API de GitHub (para listar sus versiones).</summary>
        public static string ListingUrl(string id)
        {
            if (!IsSafePackageId(id)) return null;
            return ApiBase + char.ToLowerInvariant(id[0]) + "/" + id.Replace('.', '/') + "?ref=master";
        }

        public static bool IsGitHubRaw(Uri u) { return Http.IsHttps(u) && u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase); }
        public static bool IsGitHubApi(Uri u) { return Http.IsHttps(u) && u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Versiones publicadas (carpetas) según la respuesta de la API de GitHub.</summary>
        public static List<string> ParseListing(string json)
        {
            var result = new List<string>();
            var items = Json.Parse(json) as List<object>;
            if (items == null) return result;
            foreach (object o in items)
            {
                if (!string.Equals(Json.Str(o, "type"), "dir", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Json.Str(o, "name");
                if (IsSafeVersion(name) && VersionUtil.Parse(name) != null) result.Add(name);
            }
            return result;
        }

        /// <summary>La más alta; si <paramref name="prefix"/> no está vacío (versión recortada por winget, «2.3.11…»), la más alta que empiece así.</summary>
        public static string PickVersion(List<string> versions, string prefix)
        {
            string best = null;
            foreach (string v in versions)
            {
                if (!string.IsNullOrEmpty(prefix) && !v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || VersionUtil.IsNewer(v, best)) best = v;
            }
            return best;
        }

        static readonly string[] Runnable = { "exe", "nullsoft", "inno", "msi", "wix", "burn" };

        /// <summary>Tipos ejecutables directamente y archivos ZIP con un instalador anidado ejecutable.</summary>
        public static bool IsRunnableType(string type)
        {
            return Array.IndexOf(Runnable, (type ?? "").ToLowerInvariant()) >= 0;
        }

        public static bool IsRunnableType(WingetInstaller installer)
        {
            if (installer == null) return false;
            if (!installer.IsArchive) return IsRunnableType(installer.Type);
            return IsRunnableType(installer.NestedType) && !string.IsNullOrEmpty(installer.NestedFile);
        }

        /// <summary>
        /// Qué tan bien encaja la arquitectura del instalador: -1 si el equipo no puede ejecutarlo;
        /// si no, primero la del programa instalado, después la nativa, x64 emulado en ARM64, x86.
        /// </summary>
        public static int ArchRank(string arch, string native, string installed)
        {
            string raw = (arch ?? "").Trim().ToLowerInvariant();
            string a = BrowserCatalog.NormalizeArch(raw);
            if (raw.Length == 0 || raw == "neutral") a = "neutral";
            else if (a.Length == 0) return -1;                // «arm» de 32 bits u otra que este equipo no ejecuta
            string nat = BrowserCatalog.NormalizeArch(native);
            if (nat.Length == 0) nat = "x64";
            bool compatible = a == "neutral" || a == "x86" || a == nat || (nat == "arm64" && a == "x64");
            if (!compatible) return -1;
            if (a == BrowserCatalog.NormalizeArch(installed)) return 10;
            if (a == nat) return 8;
            if (a == "x64") return 6;
            if (a == "neutral") return 5;
            return 4;
        }

        /// <summary>
        /// El instalador a usar. Primero, el mismo que eligió winget: su dirección aparece en la salida
        /// («Descargando https://…»). Si no, el que mejor encaja con lo instalado: mismo alcance
        /// (equipo/usuario), misma tecnología (MSI o no), arquitectura y, al final, idioma.
        /// </summary>
        public static WingetInstaller Choose(WingetManifest m, string wingetOutput, string nativeArch, string installedArch,
                                             bool installedPerMachine, bool installedIsMsi, out string why)
        {
            bool otherScope;
            return Choose(m, wingetOutput, nativeArch, installedArch, installedPerMachine, installedIsMsi, false, null, out why, out otherScope);
        }

        /// <summary>
        /// Tipos que deciden solos dónde instalar (leen la carpeta de la instalación anterior): el alcance
        /// «equipo/usuario» del manifiesto es una declaración, no un interruptor. Un MSI, en cambio, instala
        /// otra copia en el otro alcance.
        /// </summary>
        public static bool ScopeAgnosticType(WingetInstaller i)
        {
            if (i == null || i.IsMsi) return false;
            string t = (i.EffectiveType ?? "").ToLowerInvariant();
            return t == "nullsoft" || t == "inno";
        }

        /// <summary>
        /// Igual, para cuando winget dijo que no hay instalador aplicable: <paramref name="scopeFlexible"/>
        /// admite el otro alcance en los instaladores NSIS e Inno Setup (<paramref name="otherScope"/> lo
        /// indica), y <paramref name="osVersion"/> descarta los que piden un Windows más nuevo.
        /// </summary>
        public static WingetInstaller Choose(WingetManifest m, string wingetOutput, string nativeArch, string installedArch,
                                             bool installedPerMachine, bool installedIsMsi, bool scopeFlexible, string osVersion,
                                             out string why, out bool otherScope)
        {
            why = null;
            otherScope = false;
            if (m == null || m.Installers.Count == 0) { why = "el manifiesto no tiene instaladores"; return null; }
            var pool = new List<WingetInstaller>();
            if (!string.IsNullOrEmpty(wingetOutput))
                foreach (var i in m.Installers)
                    if (OutputMentions(wingetOutput, i.Url)) pool.Add(i);
            bool fromWinget = pool.Count > 0;
            if (!fromWinget) pool.AddRange(m.Installers);

            WingetInstaller best = null;
            int bestScore = int.MinValue;
            bool bestOther = false;
            string firstType = null, minOs = null;
            bool archOk = false, scopeBlocked = false, msiScopeBlocked = false;
            foreach (var i in pool)
            {
                if (!IsRunnableType(i)) { if (firstType == null) firstType = i.Type; continue; }
                int arch = ArchRank(i.Architecture, nativeArch, installedArch);
                if (arch < 0) continue;
                archOk = true;
                if (!string.IsNullOrEmpty(osVersion) && VersionUtil.Parse(i.MinOs) != null && VersionUtil.Parse(osVersion) != null &&
                    VersionUtil.Compare(i.MinOs, osVersion) > 0)
                {
                    if (minOs == null || VersionUtil.Compare(i.MinOs, minOs) < 0) minOs = i.MinOs;
                    continue;
                }
                int score = arch * 2;
                bool other = i.Scope == (installedPerMachine ? "user" : "machine");
                if (other)
                {
                    // Otro alcance: sólo si winget eligió esa dirección (entonces sabe algo que acá no), o si
                    // el instalador decide solo dónde instalar y winget no tenía otro.
                    if (!fromWinget && !(scopeFlexible && ScopeAgnosticType(i)))
                    {
                        scopeBlocked = true;
                        if (i.IsMsi) msiScopeBlocked = true;
                        continue;
                    }
                    score -= 30;
                }
                if (i.IsMsi == installedIsMsi) score += 20;
                if (i.Scope.Length > 0) score += 1;
                string loc = (i.Locale ?? "").ToLowerInvariant();
                if (loc.StartsWith("es")) score += 2;
                else if (loc.Length == 0 || loc == "en-us") score += 1;
                if (score > bestScore) { bestScore = score; best = i; bestOther = other && !fromWinget; }
            }
            otherScope = best != null && bestOther;
            if (best == null)
            {
                if (minOs != null && !scopeBlocked)
                    why = "la versión nueva requiere Windows " + WindowsName(minOs) + " o posterior, y este equipo tiene una versión anterior";
                else if (scopeBlocked)
                    why = installedPerMachine
                        ? "la versión nueva sólo se publica para instalar por usuario y el programa está instalado para todo el equipo" +
                          (msiScopeBlocked ? " (es un MSI: instalarlo así dejaría dos copias)" : "") +
                          ". Desinstalalo desde Configuración › Aplicaciones e instalalo de nuevo"
                        : "la versión nueva sólo se publica para instalar en todo el equipo y el programa está instalado sólo para tu usuario" +
                          (msiScopeBlocked ? " (es un MSI: instalarlo así dejaría dos copias)" : "") +
                          ". Desinstalalo desde Configuración › Aplicaciones e instalalo de nuevo (como administrador)";
                else if (firstType != null)
                    why = "el instalador " + (fromWinget ? "que usa winget " : "") + "es de tipo «" + firstType + "», que sólo puede instalar winget";
                else if (!archOk)
                    why = "el manifiesto no publica un instalador para la arquitectura de este equipo";
                else
                    why = "ningún instalador del manifiesto sirve para este equipo";
            }
            return best;
        }

        /// <summary>
        /// «10.0.22000.0» → «11 21H2»; «10.0.17763.0» → «10 1809». Sale de la tabla única de versiones
        /// (<see cref="WindowsReleases"/>): una compilación que no está ahí se nombra por su número, sin tomar el
        /// nombre de la versión anterior («10.0.27800.0» → «11 (compilación 27800)»).
        /// </summary>
        public static string WindowsName(string version)
        {
            long[] v = VersionUtil.Parse(version);
            if (v == null || v.Length < 3 || v[0] != 10 || v[2] > int.MaxValue) return version ?? "";
            return WindowsReleases.ShortName((int)v[2]);
        }

        /// <summary>
        /// ¿La salida de winget nombra exactamente esta dirección? Tiene que terminar ahí (espacio o
        /// fin de línea): «…/setup.exe» no cuenta dentro de «…/setup.exe?v=2».
        /// </summary>
        public static bool OutputMentions(string output, string url)
        {
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(url)) return false;
            int from = 0;
            while (true)
            {
                int i = output.IndexOf(url, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                int end = i + url.Length;
                if (end == output.Length || char.IsWhiteSpace(output[end])) return true;
                from = i + 1;
            }
        }

        /// <summary>Interruptores predeterminados de winget para cada tecnología (los mismos que usa winget).</summary>
        public static string DefaultSwitch(string type, bool silent)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "msi":
                case "wix":
                case "burn": return silent ? "/quiet /norestart" : "/passive /norestart";
                case "nullsoft": return "/S";
                case "inno": return silent ? "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : "/SP- /SILENT /SUPPRESSMSGBOXES /NORESTART";
                default: return null;
            }
        }

        /// <summary>
        /// 2.0.44: ¿el manifiesto no da ninguna forma de instalarlo en silencio? (ni Silent, ni SilentWithProgress, ni los
        /// interruptores de su tecnología, ni InstallModes «silent»). Con su ventana (modo interactivo) sí se puede.
        /// </summary>
        public static bool HasNoSilentMode(WingetInstaller inst)
        {
            if (inst == null) return false;
            return inst.Switch("Silent") == null && inst.Switch("SilentWithProgress") == null &&
                   DefaultSwitch(inst.EffectiveType, true) == null && !inst.InstallModes.Contains("silent");
        }

        static readonly Regex SafeSwitches = new Regex(@"^[\x20-\x7E]{0,400}$", RegexOptions.Compiled);

        /// <summary>
        /// Argumentos del instalador, como los arma winget: el modo (silencioso o interactivo), más
        /// Custom y Upgrade. Para MSI, msiexec /i. null (con el motivo) si no se puede.
        /// </summary>
        public static string BuildArguments(WingetInstaller inst, string file, bool interactive, string msiLog, out bool useMsiexec, out string why)
        {
            why = null;
            useMsiexec = inst.IsMsi;
            string mode;
            if (interactive) mode = inst.Switch("Interactive") ?? (inst.IsMsi ? "/norestart" : "");
            else
            {
                // Igual que winget: Silent, si no SilentWithProgress, si no los de la tecnología. Un
                // «exe» sin interruptores sólo si el manifiesto declara que se instala en silencio
                // (como Chrome): winget lo ejecutaría igual, pero podría quedar esperando una ventana.
                mode = inst.Switch("Silent") ?? inst.Switch("SilentWithProgress") ?? DefaultSwitch(inst.EffectiveType, true);
                if (mode == null && inst.InstallModes.Contains("silent")) mode = "";
            }
            if (mode == null) { why = "el manifiesto no indica cómo instalarlo en silencio (marcá «Mostrar la ventana de cada instalador» para hacerlo con sus ventanas)"; return null; }
            var parts = new List<string>();
            if (useMsiexec) { parts.Add("/i"); parts.Add(ProcessRunner.QuoteArg(file)); }
            foreach (string p in new[] { mode, inst.Switch("Custom"), inst.Switch("Upgrade") })
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!SafeSwitches.IsMatch(p) || p.IndexOf('<') >= 0)
                {
                    why = "el manifiesto trae interruptores que no se pueden usar fuera de winget (" + Fmt.Truncate(p, 60) + ")";
                    return null;
                }
                parts.Add(p.Trim());
            }
            if (useMsiexec && !string.IsNullOrEmpty(msiLog)) { parts.Add("/l*v"); parts.Add(ProcessRunner.QuoteArg(msiLog)); }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>Traduce el código de salida según el manifiesto (InstallerSuccessCodes y ExpectedReturnCodes).</summary>
        public static InstallerExit InterpretExit(WingetInstaller inst, long code)
        {
            var r = new InstallerExit();
            if (code == 0 || (inst != null && inst.SuccessCodes.Contains(code))) { r.Success = true; return r; }
            if (code == 3010 || code == 1641) { r.Success = true; r.Reboot = true; return r; }
            string response;
            if (inst != null && inst.ReturnCodes.TryGetValue(code, out response))
            {
                switch ((response ?? "").ToLowerInvariant())
                {
                    case "rebootrequiredtofinish":
                    case "rebootinitiated": r.Success = true; r.Reboot = true; return r;
                    case "alreadyinstalled": r.Success = true; r.Message = "el instalador informa que esa versión ya está instalada"; return r;
                    case "packageinuse":
                    case "packageinusebyapplication": r.Message = "el programa está abierto; cerralo y reintentá"; return r;
                    case "fileinuse": r.Message = "un archivo del programa está en uso; cerralo y reintentá"; return r;
                    case "installinprogress": r.Message = "hay otra instalación en curso; esperá a que termine y reintentá"; return r;
                    case "missingdependency": r.Message = "falta una dependencia del programa"; return r;
                    case "diskfull": r.Message = "no hay espacio en el disco"; return r;
                    case "insufficientmemory": r.Message = "memoria insuficiente"; return r;
                    case "invalidparameter": r.Message = "el instalador rechazó los parámetros de instalación silenciosa; probá con «Mostrar la ventana de cada instalador»"; return r;
                    case "nonetwork": r.Message = "el instalador necesita conexión a Internet"; return r;
                    case "contactsupport": r.Message = "el instalador pide contactar al soporte del fabricante"; return r;
                    case "rebootrequiredforinstall": r.RestartFirst = true; r.Message = "hay que reiniciar el equipo antes de instalarlo: reiniciá y volvé a actualizarlo"; return r;
                    case "cancelledbyuser": r.Message = "se canceló en el instalador"; return r;
                    case "downgrade": r.Message = "el instalador no instala sobre una versión más nueva"; return r;
                    case "blockedbypolicy": r.Message = "una directiva del equipo bloquea la instalación"; return r;
                    case "systemnotsupported": r.Message = "el programa no es compatible con este sistema"; return r;
                }
            }
            if (inst != null && inst.IsMsi)
                switch (code)
                {
                    case 1602: r.Message = "se canceló en el instalador"; return r;
                    case 1618: r.Message = "hay otra instalación en curso; esperá a que termine y reintentá"; return r;
                    case 1625: r.Message = "una directiva del equipo bloquea la instalación"; return r;
                    case 1638: r.Message = "Windows Installer informa otra versión instalada"; return r;
                }
            r.Message = "el instalador devolvió el código " + code + "; suele significar que no acepta instalarse en silencio o que el programa está abierto. Probá con «Mostrar la ventana de cada instalador»";
            return r;
        }

        // ---- Firmantes ----

        static readonly Regex SubjectField = new Regex(@"(?:^|,)\s*(?<k>O|CN)=(?:""(?<v>(?:[^""]|"""")*)""|(?<v>[^,]*))", RegexOptions.Compiled);

        /// <summary>Organización (O=) y nombre común (CN=) del sujeto de un certificado, en ese orden.</summary>
        public static List<string> SignerNames(string subject)
        {
            var o = new List<string>();
            var cn = new List<string>();
            if (!string.IsNullOrEmpty(subject))
                foreach (Match m in SubjectField.Matches(subject))
                {
                    string v = m.Groups["v"].Value.Replace("\"\"", "\"").Trim();
                    if (v.Length == 0) continue;
                    (m.Groups["k"].Value == "O" ? o : cn).Add(v);
                }
            o.AddRange(cn);
            return o;
        }

        static readonly HashSet<string> LegalSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "inc", "incorporated", "llc", "ltd", "limited", "co", "corp", "corporation", "company", "gmbh", "ag", "sa", "sas",
            "sarl", "srl", "sl", "spa", "bv", "nv", "oy", "ab", "as", "aps", "kg", "se", "plc", "pty", "kk", "ltda", "lda", "sro", "kft"
        };

        /// <summary>Nombre de empresa comparable: minúsculas, sin puntuación y sin la forma jurídica final («GOM &amp; Company» → «gom»).</summary>
        public static string NormalizeCompany(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder();
            foreach (char ch in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            var tokens = new List<string>(sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[tokens.Count - 1])) tokens.RemoveAt(tokens.Count - 1);
            return string.Join(" ", tokens.ToArray());
        }

        static string Collapse(string s) { return Regex.Replace((s ?? "").Trim(), @"\s+", " "); }

        /// <summary>
        /// ¿Se acepta un instalador cuyo hash no coincide con el catálogo? Sólo si su firma válida es
        /// del MISMO firmante que los ejecutables del programa instalado; si el programa instalado no
        /// tiene ejecutables firmados, del editor que figura en su registro. null si se acepta
        /// (<paramref name="how"/> dice por qué); si no, el motivo.
        /// </summary>
        public static string MatchSigner(string subject, ICollection<string> installedSigners, string publisher, out string how)
        {
            how = null;
            var names = SignerNames(subject);
            if (names.Count == 0) return "no se pudo leer quién firmó el instalador";
            if (installedSigners != null && installedSigners.Count > 0)
            {
                foreach (string n in names)
                    foreach (string s in installedSigners)
                        if (Collapse(n).Equals(Collapse(s), StringComparison.OrdinalIgnoreCase))
                        {
                            how = "firmado por " + n + ", igual que el programa instalado";
                            return null;
                        }
                string first = null;
                foreach (string s in installedSigners) { first = s; break; }
                return "está firmado por «" + names[0] + "» y el programa instalado por «" + first + "»";
            }
            string pub = NormalizeCompany(publisher);
            if (pub.Length >= 3)
            {
                foreach (string n in names)
                    if (NormalizeCompany(n) == pub)
                    {
                        how = "firmado por " + n + ", el editor registrado del programa";
                        return null;
                    }
                return "está firmado por «" + names[0] + "», que no es el editor del programa instalado («" + publisher + "»)";
            }
            return "el programa instalado no tiene ejecutables firmados ni editor registrado con los cuales comparar la firma";
        }

        /// <summary>Sólo letras y dígitos, en minúsculas: «GOM &amp; Company» y «GOM&amp;Company» → «gomcompany».</summary>
        public static string CompactCompany(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>
        /// Empresa que cambió de nombre (GOM Player: los ejecutables instalados están firmados por
        /// «GRETECH CORPORATION» y el instalador nuevo por «GOM&amp;Company», que es la misma empresa con su
        /// nombre actual). Se acepta SÓLO si el firmante del instalador nuevo coincide exactamente (letras
        /// y dígitos, sin quitar la forma jurídica) con el editor que figura en el registro del programa
        /// instalado (clave de equipo, que sólo escribe un administrador) Y con el editor del manifiesto
        /// oficial de winget, que revisan sus moderadores. Dos fuentes independientes tienen que decir lo
        /// mismo que el certificado. null si se acepta (<paramref name="how"/> dice por qué); si no, el motivo.
        /// </summary>
        public static string MatchRenamedPublisher(string subject, ICollection<string> installedSigners, string registryPublisher, string manifestPublisher, out string how)
        {
            how = null;
            var names = SignerNames(subject);
            if (names.Count == 0) return "no se pudo leer quién firmó el instalador";
            string reg = CompactCompany(registryPublisher);
            string man = CompactCompany(manifestPublisher);
            if (reg.Length < 3) return "no hay un editor registrado confiable con el cual comparar";
            if (man.Length < 3) return "no se pudo leer el editor en el manifiesto oficial de winget";
            if (reg != man) return "el editor del registro («" + registryPublisher + "») no coincide con el del manifiesto oficial («" + manifestPublisher + "»)";
            foreach (string n in names)
                if (CompactCompany(n) == reg)
                {
                    string before = null;
                    if (installedSigners != null) foreach (string s in installedSigners) { before = s; break; }
                    how = "firmado por " + n + ", el editor que figura en el registro del programa y en el manifiesto oficial de winget" +
                          (before != null ? " (lo instalado estaba firmado con el nombre anterior, " + before + ")" : "");
                    return null;
                }
            return "está firmado por «" + names[0] + "», que no es el editor del registro ni del manifiesto («" + registryPublisher + "»)";
        }
    }
}
