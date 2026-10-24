using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BreezySRPG95.Runtime;

/// <summary>
/// Keeps the sound on Windows' default output: when it changes — headphones plugged in, another device picked from the
/// taskbar — OpenAL Soft says so (ALC_SOFT_system_events, on a thread of its own), and the device MonoGame opened is moved
/// to the new output in place (ALC_SOFT_reopen_device), the sounds and the music going on there. MonoGame's own OpenAL
/// (1.18.1) stays on the output it opened; the Windows build carries OpenAL Soft 1.25.2 in its place (BreezySRPG95.csproj).
/// Without those extensions (another OpenAL), nothing changes.
/// </summary>
static unsafe class AudioDevice
{
    const int PlaybackDevice = 0x19D4, DefaultDeviceChanged = 0x19D6, EventSupported = 0x19D9;

    static IntPtr _device;
    static delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, byte> _reopen;
    static int _changed;   // set by OpenAL's thread
    static long _moveAt = long.MaxValue;
    static int _tries;

    /// <summary>Whether the sound follows the default output (the extensions are there and the event is told).</summary>
    public static bool Following { get; private set; }

    /// <summary>Starts following, once MonoGame's sound is up (its first sound made).</summary>
    public static void Follow()
    {
        if (!OperatingSystem.IsWindows() || Following) return;
        try
        {
            var lib = GetModuleHandleW("soft_oal.dll");
            if (lib == IntPtr.Zero) return;
            var currentContext = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(lib, "alcGetCurrentContext");
            var contextsDevice = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(lib, "alcGetContextsDevice");
            var hasExtension = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)NativeLibrary.GetExport(lib, "alcIsExtensionPresent");
            _device = contextsDevice(currentContext());
            bool Has(IntPtr device, string name)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* p = bytes) return hasExtension(device, p) != 0;
            }
            if (_device == IntPtr.Zero || !Has(_device, "ALC_SOFT_reopen_device") || !Has(IntPtr.Zero, "ALC_SOFT_system_events")) return;
            // an extension's functions come from alcGetProcAddress, not the DLL's exports
            var procAddress = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)NativeLibrary.GetExport(lib, "alcGetProcAddress");
            IntPtr Function(string name)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* p = bytes)
                {
                    var f = procAddress(_device, p);
                    return f != IntPtr.Zero ? f : throw new EntryPointNotFoundException(name);
                }
            }
            var supported = (delegate* unmanaged[Cdecl]<int, int, int>)Function("alcEventIsSupportedSOFT");
            if (supported(DefaultDeviceChanged, PlaybackDevice) != EventSupported) return;
            _reopen = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, byte>)Function("alcReopenDeviceSOFT");
            var callback = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, IntPtr, int, byte*, void*, void>, void*, void>)
                Function("alcEventCallbackSOFT");
            var control = (delegate* unmanaged[Cdecl]<int, int*, byte, byte>)Function("alcEventControlSOFT");
            callback(&OnEvent, null);
            int changed = DefaultDeviceChanged;
            Following = control(1, &changed, 1) != 0;
        }
        catch (Exception) { Following = false; }   // another OpenAL: the sound stays where it opened
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnEvent(int eventType, int deviceType, IntPtr device, int length, byte* message, void* user)
    {
        if (eventType == DefaultDeviceChanged && deviceType == PlaybackDevice) Volatile.Write(ref _changed, 1);
    }

    /// <summary>
    /// Call every frame: a moment after the default output changed (Windows may still be setting it up), the sound moves
    /// to it; should that fail, it's tried again each second, five times.
    /// </summary>
    public static void Update()
    {
        if (!Following) return;
        if (Interlocked.Exchange(ref _changed, 0) == 1) (_moveAt, _tries) = (Environment.TickCount64 + 200, 0);
        if (Environment.TickCount64 < _moveAt) return;
        bool moved = Move();
        _moveAt = moved || ++_tries >= 5 ? long.MaxValue : Environment.TickCount64 + 1000;
    }

    /// <summary>Moves the sound to the default output now; whether it went.</summary>
    internal static bool Move() => Following && _reopen(_device, null, null) != 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);
}
