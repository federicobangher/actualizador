// Contrato común de los proveedores de actualizaciones.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    public interface IUpdateProvider
    {
        string Name { get; }
        /// <summary>null si el proveedor puede usarse; si no, el motivo.</summary>
        string Unavailable(UpdateContext ctx);
        void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes);
        void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed);
    }
}
