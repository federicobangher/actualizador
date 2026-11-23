// Permisos de archivos y carpetas: lo que define la empresa sólo vale si únicamente un administrador puede cambiarlo.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Actualizador.Core.Security
{
    public static class AclGuard
    {
        /// <summary>Sólo para las pruebas: decide sin mirar los permisos reales.</summary>
        internal static Func<string, bool> AdminOnlyForTests;

        const FileSystemRights WriteLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
                                           FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                                           FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>
        /// ¿Sólo SYSTEM, Administradores o TrustedInstaller pueden modificar el archivo (y su carpeta)? Si un usuario común
        /// pudiera escribirlo, podría cambiar la política o borrar rastros: no se le cree.
        /// </summary>
        public static bool IsAdminOnlyWritable(string path)
        {
            if (AdminOnlyForTests != null) return AdminOnlyForTests(path);
            try
            {
                if (File.Exists(path))
                {
                    if (!CheckSecurity(GetFileSecurity(path))) return false;
                    string dir = Path.GetDirectoryName(path);
                    return string.IsNullOrEmpty(dir) || CheckSecurity(GetDirectorySecurity(dir));
                }
                if (Directory.Exists(path)) return CheckSecurity(GetDirectorySecurity(path));
                return false;
            }
            catch (Exception ex) { Log.Quiet("Permisos de " + path, ex); return false; }
        }

        static FileSystemSecurity GetFileSecurity(string path)
        {
#if NET5_0_OR_GREATER
            return new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
#else
            return File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner);
#endif
        }

        static FileSystemSecurity GetDirectorySecurity(string path)
        {
#if NET5_0_OR_GREATER
            return new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
#else
            return Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner);
#endif
        }

        static bool CheckSecurity(FileSystemSecurity sec)
        {
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !IsTrusted(owner)) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteLike) == 0) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null || !IsTrusted(sid)) return false;
            }
            return true;
        }

        /// <summary>SYSTEM, Administradores, TrustedInstaller o CREATOR OWNER (que hereda del dueño, ya verificado).</summary>
        public static bool IsTrusted(SecurityIdentifier sid)
        {
            if (sid == null) return false;
            if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) return true;
            return sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";   // NT SERVICE\TrustedInstaller
        }

        /// <summary>
        /// Crea la carpeta (si falta) con permisos sólo para SYSTEM y Administradores, más lectura para los usuarios si
        /// <paramref name="usersCanRead"/>. Si ya existe con permisos más abiertos, no se usa (devuelve false).
        /// </summary>
        public static bool EnsureAdminDirectory(string dir, bool usersCanRead)
        {
            try
            {
                if (Directory.Exists(dir)) return IsAdminOnlyWritable(dir);
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                if (usersCanRead)
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
#if NET5_0_OR_GREATER
                new DirectoryInfo(dir).Create(sec);
#else
                Directory.CreateDirectory(dir, sec);
#endif
                return true;
            }
            catch (Exception ex) { Log.Warn("No se pudo crear " + dir + " con permisos de administrador: " + ex.Message); return false; }
        }
    }
}
