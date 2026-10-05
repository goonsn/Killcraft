using System;
using System.Runtime.InteropServices;

namespace Killcraft
{
    // Minecraft's sound, muted while ULTRAKILL is paused: freezing Minecraft's world stops its mobs and
    // TNT, but its client still plays some sounds (fire crackling, sounds already started). Muted the
    // way Windows' volume mixer does it, for Minecraft's process only.
    internal static class McAudio
    {
        private static bool failed;

        public static void SetMuted(uint pid, bool muted)
        {
            if (failed || pid == 0)
            {
                return;
            }
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out IMMDevice device));
                Guid iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o));
                var manager = (IAudioSessionManager2)o;
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions));
                Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
                int changed = 0;
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl2 session) != 0 || session == null)
                    {
                        continue;
                    }
                    if (session.GetProcessId(out uint sessionPid) == 0 && sessionPid == pid && session is ISimpleAudioVolume volume)
                    {
                        Guid context = Guid.Empty;
                        volume.SetMute(muted, ref context);
                        changed++;
                    }
                    Marshal.ReleaseComObject(session);
                }
                if (Plugin.Diagnostics.Value)
                {
                    Plugin.Log.LogInfo($"Minecraft's sound {(muted ? "muted" : "back on")} ({changed} of {count} sessions)");
                }
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning($"couldn't mute Minecraft's sound while paused: {e.Message}");
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int count);
            int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(IntPtr name, ref Guid context);
            int GetIconPath(out IntPtr path);
            int SetIconPath(IntPtr path, ref Guid context);
            int GetGroupingParam(out Guid grouping);
            int SetGroupingParam(ref Guid grouping, ref Guid context);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            int GetSessionIdentifier(out IntPtr id);
            int GetSessionInstanceIdentifier(out IntPtr id);
            int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid context);
            int GetMasterVolume(out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        }
    }
}
