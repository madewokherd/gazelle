﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xalia.Gudl;
using Xalia.Interop;
using Xalia.UiDom;
using static Xalia.Interop.Win32;

namespace Xalia.Uia.Win32
{
    internal class Win32TabControl : Win32Element
    {
        public Win32TabControl(IntPtr hwnd, UiaConnection root) : base(hwnd, root)
        {
        }

        static Win32TabControl()
        {
            string[] aliases = {
                "selection_index", "win32_selection_index",
            };
            property_aliases = new Dictionary<string, string>(aliases.Length / 2);
            for (int i = 0; i < aliases.Length; i += 2)
            {
                property_aliases[aliases[i]] = aliases[i + 1];
            }
        }

        static Dictionary<string, string> property_aliases;
        private static readonly UiDomValue role = new UiDomEnum(new[] { "tab", "page_tab_list", "pagetablist" });

        private Win32RemoteProcessMemory remote_process_memory;
        private bool SelectionIndexKnown;
        private int SelectionIndex;

        protected override void SetAlive(bool value)
        {
            if (!value)
            {
                if (!(remote_process_memory is null))
                {
                    remote_process_memory.Unref();
                    remote_process_memory = null;
                }
            }
            base.SetAlive(value);
        }

        protected override UiDomValue EvaluateIdentifierCore(string id, UiDomRoot root, [In, Out] HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (property_aliases.TryGetValue(id, out string aliased))
            {
                var value = base.EvaluateIdentifierCore(id, root, depends_on);
                if (!value.Equals(UiDomUndefined.Instance))
                    return value;
                id = aliased;
            }

            switch (id)
            {
                case "is_win32_tab_control":
                case "is_win32_tabcontrol":
                case "tab":
                case "page_tab_list":
                case "pagetablist":
                    return UiDomBoolean.True;
                case "role":
                case "control_type":
                    return role;
                case "win32_selection_index":
                    depends_on.Add((this, new IdentifierExpression("win32_selection_index")));
                    if (SelectionIndexKnown)
                        return new UiDomInt(SelectionIndex);
                    return UiDomUndefined.Instance;

                default:
                    break;
            }

            return base.EvaluateIdentifierCore(id, root, depends_on);
        }

        protected override void DumpProperties()
        {
            if (SelectionIndexKnown)
                Console.WriteLine($"  win32_selection_index: {SelectionIndex}");
            base.DumpProperties();
        }

        protected override void WatchProperty(GudlExpression expression)
        {
            if (expression is IdentifierExpression id)
            {
                switch (id.Name)
                {
                    case "win32_selection_index":
                        PollProperty(expression, RefreshSelectionIndex, 200);
                        break;
                }
            }
            base.WatchProperty(expression);
        }

        protected override void UnwatchProperty(GudlExpression expression)
        {
            if (expression is IdentifierExpression id)
            {
                switch (id.Name)
                {
                    case "win32_selection_index":
                        EndPollProperty(expression);
                        SelectionIndexKnown = false;
                        break;
                }
            }
            base.UnwatchProperty(expression);
        }

        private async Task RefreshSelectionIndex()
        {
            IntPtr index = await SendMessageAsync(Hwnd, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
            int i = index.ToInt32();

            bool known = i >= 0;

            if (known != SelectionIndexKnown || i != SelectionIndex)
            {
                if (known)
                {
                    SelectionIndexKnown = true;
                    SelectionIndex = i;
                    PropertyChanged("win32_selection_index", i);
                }
                else
                {
                    SelectionIndexKnown = false;
                    PropertyChanged("win32_selection_index", "undefined");
                }
            }
        }
    }
}
