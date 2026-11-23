// 2.0.25: páginas oficiales leídas hace un rato, para no bajarlas de nuevo en la búsqueda siguiente.
using System;
using System.Collections.Generic;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Pedido del usuario (23/11/2026): «tarda mucho en buscar y en instalar». Después de instalar, «todo de una vez» vuelve a buscar
    /// (hasta 3 vueltas) y cada vuelta volvía a bajar TODAS las páginas oficiales y las versiones de los navegadores, aunque se habían
    /// leído minutos antes. Lo que publica un fabricante no cambia en unos minutos: se guarda en memoria lo leído en los últimos
    /// <see cref="Life"/> (sólo respuestas válidas; un error se vuelve a intentar). Tiene un tope de tamaño, para equipos con poca memoria.
    /// </summary>
    public static class RecentPages
    {
        /// <summary>Cuánto vale una página leída.</summary>
        public static TimeSpan Life = TimeSpan.FromMinutes(20);
        /// <summary>Tope de lo guardado (caracteres; unos 16 MB de memoria).</summary>
        public const long MaxChars = 8L * 1024 * 1024;
        /// <summary>Una página más grande que esto no se guarda.</summary>
        public const int MaxPageChars = 2 * 1024 * 1024;

        sealed class Entry { public DateTime At; public string Text; }
        static readonly Dictionary<string, Entry> pages = new Dictionary<string, Entry>(StringComparer.Ordinal);
        static long total;
        static int hits;

        /// <summary>Cuántas lecturas se ahorraron en esta ejecución (para el registro).</summary>
        public static int Hits { get { lock (pages) return hits; } }

        /// <summary>La página guardada si es reciente; si no, <paramref name="fetch"/> y se guarda.</summary>
        public static string Get(string key, Func<string> fetch)
        {
            if (string.IsNullOrEmpty(key)) return fetch();
            lock (pages)
            {
                Entry e;
                if (pages.TryGetValue(key, out e))
                {
                    if (DateTime.UtcNow - e.At < Life && DateTime.UtcNow >= e.At) { hits++; return e.Text; }
                    pages.Remove(key);
                    total -= e.Text.Length;
                }
            }
            string text = fetch();
            if (!string.IsNullOrEmpty(text) && text.Length <= MaxPageChars) Put(key, text);
            return text;
        }

        static void Put(string key, string text)
        {
            lock (pages)
            {
                Entry old;
                if (pages.TryGetValue(key, out old)) { total -= old.Text.Length; pages.Remove(key); }
                // Por encima del tope se quitan las más viejas.
                while (total + text.Length > MaxChars && pages.Count > 0)
                {
                    string oldest = null;
                    DateTime at = DateTime.MaxValue;
                    foreach (var kv in pages) if (kv.Value.At < at) { at = kv.Value.At; oldest = kv.Key; }
                    total -= pages[oldest].Text.Length;
                    pages.Remove(oldest);
                }
                pages[key] = new Entry { At = DateTime.UtcNow, Text = text };
                total += text.Length;
            }
        }

        /// <summary>Lo guardado ocupa (caracteres). Para las pruebas.</summary>
        public static long Size { get { lock (pages) return total; } }

        /// <summary>Olvida todo (Opciones › «Olvidar las consultas guardadas», y las pruebas).</summary>
        public static void Clear()
        {
            lock (pages) { pages.Clear(); total = 0; hits = 0; }
        }
    }
}
