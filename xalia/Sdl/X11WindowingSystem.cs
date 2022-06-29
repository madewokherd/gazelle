﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SDL2;

using static Xalia.Interop.X11;

namespace Xalia.Sdl
{
    internal class X11WindowingSystem : XdgWindowingSystem
    {
        internal IntPtr display;
        private IntPtr root_window;

        private float dpi;
        private bool dpi_checked;

        private bool xtest_supported;


        public X11WindowingSystem()
        {
            IntPtr window = SDL.SDL_CreateWindow("dummy window", 0, 0, 1, 1, SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN);

            if (window == IntPtr.Zero)
                throw new Exception(SDL.SDL_GetError());

            SDL.SDL_SysWMinfo info = default;

            SDL.SDL_VERSION(out info.version);

            if (SDL.SDL_GetWindowWMInfo(window, ref info) != SDL.SDL_bool.SDL_TRUE)
                throw new Exception(SDL.SDL_GetError());

            SDL.SDL_DestroyWindow(window);

            display = info.info.x11.display;

            root_window = XDefaultRootWindow(display);

            try
            {
                int _ev = 0, _er = 0, _maj = 0, _min = 0;
                var supported = XTestQueryExtension(display, ref _ev, ref _er, ref _maj, ref _min);

                xtest_supported = (supported != False);
            }
            catch (DllNotFoundException)
            {
                // no libXtst
            }

            Console.WriteLine($"Xtest: {xtest_supported}");

            EnableInputMask(root_window, PropertyChangeMask);
            
            ((SdlSynchronizationContext)SynchronizationContext.Current).SdlEvent += OnSdlEvent;

            SDL.SDL_EventState(SDL.SDL_EventType.SDL_SYSWMEVENT, SDL.SDL_ENABLE);
        }

        private void OnSdlEvent(object sender, SdlSynchronizationContext.SdlEventArgs e)
        {
            if (e.SdlEvent.type == SDL.SDL_EventType.SDL_SYSWMEVENT)
            {
                var syswm = Marshal.PtrToStructure<SDL_SysWMmsg_X11>(e.SdlEvent.syswm.msg);

                if (syswm.xev.type == PropertyNotify && syswm.xev.xproperty.window == root_window &&
                    syswm.xev.xproperty.atom == XA_RESOURCE_MANAGER)
                {
                    dpi_checked = false;
                }
            }
        }

        private void EnableInputMask(IntPtr window, IntPtr mask)
        {
            XWindowAttributes window_attributes = default;
            XGetWindowAttributes(display, window, ref window_attributes);

            IntPtr prev_event_mask = window_attributes.your_event_mask;

            if (((long)prev_event_mask & (long)mask) != (long)mask)
            {
                XSelectInput(display, window, (IntPtr)((long)prev_event_mask | (long)mask));
            }
        }

        private void CheckDpi()
        {
            dpi_checked = true;
            int length = 1024;

            string resources;

            while (true)
            {
                var result = XGetWindowProperty(display, root_window, XA_RESOURCE_MANAGER, IntPtr.Zero, (IntPtr)length,
                    False, XA_STRING, out var actual_type, out var actual_format, out var nitems, out var bytes_after,
                    out var prop);
                if (result != Success || actual_type != XA_STRING || actual_format != 8)
                {
                    dpi = 0;
                    return;
                }

                if (bytes_after != UIntPtr.Zero)
                {
                    length += ((int)bytes_after + 3)/4;
                    continue;
                }

                resources = Marshal.PtrToStringAnsi(prop);
                XFree(prop);
                break;
            }

            foreach (var line in resources.Split('\n'))
            {
                if (line.StartsWith("Xft.dpi:\t"))
                {
                    if (int.TryParse(line.Substring(9), out int dpi_int))
                    {
                        dpi = dpi_int;
                        return;
                    }
                }
            }

            dpi = 0;
        }

        public override float GetDpi(int x, int y)
        {
            if (!dpi_checked)
            {
                CheckDpi();
            }

            if (dpi != 0.0)
                return dpi;

            return base.GetDpi(x, y);
        }

        public override bool CanSendKeys => xtest_supported || base.CanSendKeys;

        public override Task SendKey(string key)
        {
            if (xtest_supported)
            {
                return SendKey(XKeyCodes.GetKeySym(key));
            }
            return base.SendKey(key);
        }

        public override async Task SendKey(int keysym)
        {
            if (xtest_supported)
            {
                int keycode = XKeysymToKeycode(display, new IntPtr(keysym)).ToInt32();
                if (keycode == 0)
                {
                    Console.WriteLine($"WARNING: Failed looking up X keycode for keysym {keysym}");
                    return;
                }
                //TODO: check XkbGetSlowKeysDelay
                XTestFakeKeyEvent(display, keycode, True, IntPtr.Zero);
                XTestFakeKeyEvent(display, keycode, False, IntPtr.Zero);
                return;
            }
            await base.SendKey(keysym);
        }
    }
}
