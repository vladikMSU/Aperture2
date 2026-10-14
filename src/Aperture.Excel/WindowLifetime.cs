using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aperture.Excel
{
    // COM Window proxies are not stable identity tokens in Excel. A native window
    // property survives proxy changes and disappears when Windows destroys the HWND.
    internal sealed class WindowLifetime : IDisposable
    {
        private static int nextGeneration;
        private readonly IntPtr hwnd;
        private readonly IntPtr generation;
        private readonly string propertyName;
        private readonly uint processId;

        internal WindowLifetime(int windowHwnd, string instanceId)
        {
            hwnd = new IntPtr(windowHwnd);
            processId = (uint)Process.GetCurrentProcess().Id;
            propertyName = "Aperture2.Window." + instanceId;
            generation = new IntPtr(Interlocked.Increment(ref nextGeneration));
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != processId) throw new InvalidOperationException("Окно не принадлежит этому экземпляру Excel.");
            if (!SetProp(hwnd, propertyName, generation)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal bool IsCurrent
        {
            get
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                return owner == processId && GetProp(hwnd, propertyName) == generation;
            }
        }

        public void Dispose()
        {
            if (GetProp(hwnd, propertyName) == generation) RemoveProp(hwnd, propertyName);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr RemoveProp(IntPtr hwnd, string name);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
