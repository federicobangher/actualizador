// Una sola instalación a la vez en el equipo.
using System;
using System.Threading;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Cerrojo de instalación para todo el equipo (mutex «Global\Actualizador.Instalacion»), compartido por la ventana, la
    /// consola y cualquier otra copia abierta, también en otra sesión. Es reentrante en el mismo hilo (InstallUntilDone
    /// instala varias rondas). Si otra copia está instalando se espera un poco y, si sigue, no se instala.
    /// </summary>
    public sealed class InstallLock : IDisposable
    {
        public const string MutexName = @"Global\Actualizador.Instalacion";
        public const string BusyMessage = "Otra copia de Actualizador (otra ventana, la consola o una revisión programada) está instalando en este equipo: " +
                                          "esperá a que termine y volvé a intentar. Dos instalaciones a la vez se hacen fallar entre sí.";

        /// <summary>Cuánto se espera a que la otra termine antes de desistir.</summary>
        public static TimeSpan Wait = TimeSpan.FromSeconds(5);

        Mutex mutex;
        public bool Acquired { get; private set; }

        public static InstallLock TryEnter(UpdateContext ctx)
        {
            var l = new InstallLock();
            try
            {
                l.mutex = new Mutex(false, MutexName);
                try { l.Acquired = l.mutex.WaitOne(Wait); }
                catch (AbandonedMutexException) { l.Acquired = true; }   // la otra se cerró a la fuerza: el cerrojo queda libre
            }
            catch (UnauthorizedAccessException)
            {
                // El mutex lo creó otra cuenta con más permisos: está instalando.
                l.Acquired = false;
            }
            catch (Exception ex)
            {
                // Sin mutex con nombre (no debería pasar en Windows): se instala igual, como antes.
                Log.Quiet("Cerrojo de instalación", ex);
                l.Acquired = true;
            }
            if (!l.Acquired) Log.Warn("No se instala: " + BusyMessage);
            return l;
        }

        public void Dispose()
        {
            if (mutex == null) return;
            try { if (Acquired) mutex.ReleaseMutex(); } catch (Exception ex) { Log.Quiet("Cerrojo de instalación", ex); }
            mutex.Dispose();
            mutex = null;
        }
    }
}
