﻿using System;
using System.Threading;
using System.Threading.Tasks;

using Tmds.DBus;

using Gazelle.UiDom;
using Gazelle.AtSpi.DBus;
using Gazelle.Gudl;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace Gazelle.AtSpi
{
    internal class AtSpiObject : UiDomObject
    {
        internal readonly AtSpiConnection Connection;
        internal readonly string Service;
        internal readonly string Path;
        public override string DebugId => string.Format("{0}:{1}", Service, Path);

        private bool watching_children;
        private bool children_known;
        private IDisposable children_changed_event;
        private IDisposable property_change_event;
        private IDisposable state_changed_event;
        private IDisposable bounds_changed_event;

        private bool watching_states;
        private AtSpiState state;

        private bool watching_bounds;
        public bool BoundsKnown { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        private bool watching_abs_position;
        private CancellationTokenSource abs_position_refresh_token;
        public bool AbsPositionKnown;
        public int AbsX { get; private set; }
        public int AbsY { get; private set; }

        private static GudlExpression abs_x_expr = GudlParser.ParseExpression("this_or_ancestor_matches(parent.spi_role.application).spi_abs_x + x_in_window");
        private static GudlExpression abs_y_expr = GudlParser.ParseExpression("this_or_ancestor_matches(parent.spi_role.application).spi_abs_y + y_in_window");

        internal IAccessible acc;
        internal IComponent component;

        internal IObject object_events;

        internal static readonly string[] role_names =
        {
            "invalid",
            "accelerator_label",
            "alert",
            "animation",
            "arrow",
            "calendar",
            "canvas",
            "check_box",
            "check_menu_item",
            "color_chooser",
            "column_header",
            "combo_box",
            "date_editor",
            "desktop_icon",
            "desktop_frame",
            "dial",
            "dialog",
            "directory_pane",
            "drawing_area",
            "file_chooser",
            "filler",
            "focus_traversable",
            "font_chooser",
            "frame",
            "glass_pane",
            "html_container",
            "icon",
            "image",
            "internal_frame",
            "label",
            "layered_pane",
            "list",
            "list_item",
            "menu",
            "menu_bar",
            "menu_item",
            "option_pane",
            "page_tab",
            "page_tab_list",
            "panel",
            "password_text",
            "popup_menu",
            "progress_bar",
            "push_button",
            "radio_button",
            "radio_menu_item",
            "root_pane",
            "row_header",
            "scroll_bar",
            "scroll_pane",
            "separator",
            "slider",
            "spin_button",
            "split_pane",
            "status_bar",
            "table",
            "table_cell",
            "table_column_header",
            "table_row_header",
            "tearoff_menu_item",
            "terminal",
            "text",
            "toggle_button",
            "tool_bar",
            "tool_tip",
            "tree",
            "tree_table",
            "unknown",
            "viewport",
            "window",
            "extended",
            "header",
            "footer",
            "paragraph",
            "ruler",
            "application",
            "autocomplete",
            "editbar",
            "embedded",
            "entry",
            "chart",
            "caption",
            "document_frame",
            "heading",
            "page",
            "section",
            "redundant_object",
            "form",
            "link",
            "input_method_window",
            "table_row",
            "tree_item",
            "document_spreadsheet",
            "document_presentation",
            "document_text",
            "document_web",
            "document_email",
            "comment",
            "list_box",
            "grouping",
            "image_map",
            "notification",
            "info_bar",
            "level_bar",
            "title_bar",
            "block_quote",
            "audio",
            "video",
            "definition",
            "article",
            "landmark",
            "log",
            "marquee",
            "math",
            "rating",
            "timer",
            "static",
            "math_fraction",
            "math_root",
            "subscript",
            "superscript",
            "description_list",
            "description_term",
            "description_value",
            "footnote",
            "content_deletion",
            "content_insertion",
            "mark",
            "suggestion",
        };

        private static readonly Dictionary<string, int> name_to_role;
        private static readonly UiDomEnum[] role_to_enum;

        public int Role { get; private set; }
        public bool RoleKnown { get; private set; }
        private bool fetching_role;

        static AtSpiObject()
        {
            name_to_role = new Dictionary<string, int>();
            role_to_enum = new UiDomEnum[role_names.Length];
            for (int i=0; i<role_names.Length; i++)
            {
                string name = role_names[i];
                string[] names;
                if (name == "push_button")
                    names = new[] { "push_button", "pushbutton", "button" };
                else if (name.Contains("_"))
                    names = new[] { name, name.Replace("_", "") };
                else
                    names = new[] { name };
                role_to_enum[i] = new UiDomEnum(names);
                foreach (string rolename in names)
                    name_to_role[rolename] = i;
            }
        }

        internal AtSpiObject(AtSpiConnection connection, string service, string path) : base(connection)
        {
            Path = path;
            Service = service;
            Connection = connection;
            state = new AtSpiState(this);
            acc = connection.connection.CreateProxy<IAccessible>(service, path);
            component = connection.connection.CreateProxy<IComponent>(service, path);
            object_events = connection.connection.CreateProxy<IObject>(service, path);
        }

        internal AtSpiObject(AtSpiConnection connection, string service, ObjectPath path) :
            this(connection, service, path.ToString())
        { }

        private async Task WatchProperties()
        {
            IDisposable property_change_event = await object_events.WatchPropertyChangeAsync(OnPropertyChange, Utils.OnError);
            if (this.property_change_event != null)
                this.property_change_event.Dispose();
            this.property_change_event = property_change_event;
        }

        private void OnPropertyChange((string, uint, uint, object) obj)
        {
            var propname = obj.Item1;
            var value = obj.Item4;
            switch (propname)
            {
                case "accessible-role":
                    Role = (int)value;
                    RoleKnown = true;
#if DEBUG
                    if (Role < role_to_enum.Length)
                        Console.WriteLine($"{this}.spi_role: {role_to_enum[Role]}");
                    else
                        Console.WriteLine($"{this}.spi_role: {Role}");
#endif
                    break;
            }
        }

        protected override void SetAlive(bool value)
        {
            if (value)
            {
                Utils.RunTask(WatchProperties());
            }
            else
            {
                if (children_changed_event != null)
                {
                    children_changed_event.Dispose();
                    children_changed_event = null;
                }
                watching_children = false;
                children_known = false;
                if (property_change_event != null)
                {
                    property_change_event.Dispose();
                    property_change_event = null;
                }
                RoleKnown = false;
                fetching_role = false;
                if (state_changed_event != null)
                {
                    state_changed_event.Dispose();
                    state_changed_event = null;
                }
                watching_states = false;
                if (bounds_changed_event != null)
                {
                    bounds_changed_event.Dispose();
                    bounds_changed_event = null;
                }
                watching_bounds = false;
                if (abs_position_refresh_token != null)
                {
                    abs_position_refresh_token.Cancel();
                    abs_position_refresh_token = null;
                }
                watching_abs_position = false;
            }
            base.SetAlive(value);
        }

        private async Task WatchChildrenTask()
        {
            IDisposable children_changed_event = await object_events.WatchChildrenChangedAsync(OnChildrenChanged, Utils.OnError);

            if (this.children_changed_event != null)
                this.children_changed_event.Dispose();

            this.children_changed_event = children_changed_event;

            (string, ObjectPath)[] children = await acc.GetChildrenAsync();

            if (children_known)
                return;

            for (int i=0; i<children.Length; i++)
            {
                string service = children[i].Item1;
                ObjectPath path = children[i].Item2;
                AddChild(i, new AtSpiObject(Connection, service, path));
            }
            children_known = true;
        }

        internal void WatchChildren()
        {
            if (watching_children)
                return;
#if DEBUG
            Console.WriteLine("WatchChildren for {0}", DebugId);
#endif
            watching_children = true;
            children_known = false;
            Utils.RunTask(WatchChildrenTask());
        }

        internal void UnwatchChildren()
        {
            if (!watching_children)
                return;
#if DEBUG
            Console.WriteLine("UnwatchChildren for {0}", DebugId);
#endif
            watching_children = false;
            if (children_changed_event != null)
            {
                children_changed_event.Dispose();
                children_changed_event = null;
            }
            for (int i=Children.Count-1; i >= 0; i--)
            {
                RemoveChild(i);
            }
        }

        private void OnChildrenChanged((string, uint, uint, object) obj)
        {
            if (!watching_children || !children_known)
                return;
            var detail = obj.Item1;
            var index = obj.Item2;
            var id = ((string, ObjectPath))(obj.Item4);
            var service = id.Item1;
            var path = id.Item2.ToString();
            if (detail == "add")
            {
                AddChild((int)index, new AtSpiObject(Connection, id.Item1, id.Item2));
            }
            else if (detail == "remove")
            {
                // Don't assume the index matches our internal view, we don't always get "reorder" notificaions
#if DEBUG
                bool found = false;
#endif
                for (int i=0; i<Children.Count; i++)
                {
                    var child = (AtSpiObject)Children[i];
                    if (child.Service == service && child.Path == path)
                    {
#if DEBUG
                        if (index != i)
                        {
                            Console.WriteLine("Got remove notification for {0} with index {1}, but we have it at index {2}", child.DebugId, index, i);
                        }
                        found = true;
#endif
                        RemoveChild(i);
                        break;
                    }
                }
#if DEBUG
                if (!found)
                    Console.WriteLine("Got remove notification from {0} for {1}:{2}, but we don't have it as a child",
                        DebugId, service, path);
#endif
            }
        }

        protected override void DeclarationsChanged(Dictionary<string, UiDomValue> all_declarations, HashSet<(UiDomObject, GudlExpression)> dependencies)
        {
            if (all_declarations.TryGetValue("recurse", out var recurse) && recurse.ToBool())
                WatchChildren();
            else
                UnwatchChildren();

            base.DeclarationsChanged(all_declarations, dependencies);
        }

        internal void StatesChanged(HashSet<string> changed_states)
        {
            if (changed_states.Count == 0)
                return;
            var changed_properties = new HashSet<GudlExpression>();
            foreach (string state in changed_states)
            {
                changed_properties.Add(new BinaryExpression(
                    new IdentifierExpression("spi_state"),
                    new IdentifierExpression(state),
                    GudlToken.Dot
                ));
            }
            base.PropertiesChanged(changed_properties);
        }

        private async Task FetchRole()
        {
            int role = (int)await acc.GetRoleAsync();
            Role = role;
            RoleKnown = true;
#if DEBUG
            if (role < role_to_enum.Length)
                Console.WriteLine($"{this}.spi_role: {role_to_enum[role]}");
            else
                Console.WriteLine($"{this}.spi_role: {role}");
#endif
            PropertyChanged("spi_role");
        }

        private async Task WatchBounds()
        {
            IDisposable bounds_changed_event = await object_events.WatchBoundsChangedAsync(OnBoundsChanged, Utils.OnError);
            if (this.bounds_changed_event != null)
                this.bounds_changed_event.Dispose();
            this.bounds_changed_event = bounds_changed_event;
            (X, Y, Width, Height) = await component.GetExtentsAsync(1); // 1 = ATSPI_COORD_TYPE_WINDOW
            BoundsKnown = true;
#if DEBUG
            Console.WriteLine($"{this}.spi_bounds: {X},{Y}: {Width}x{Height}");
#endif
            PropertyChanged("spi_bounds");
       }

        private void OnBoundsChanged((string, uint, uint, object) obj)
        {
            var bounds = (ValueTuple<int, int, int, int>)obj.Item4;
            (var x, var y, var width, var height) = bounds;
            if (x == X && y == Y && width == Width && height == Height)
                return;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            BoundsKnown = true;
#if DEBUG
            Console.WriteLine($"{this}.spi_bounds: {X},{Y}: {Width}x{Height}");
#endif
            PropertyChanged("spi_bounds");
        }

        protected override void WatchProperty(GudlExpression expression)
        {
            if (expression is IdentifierExpression id)
            {
                switch (id.Name)
                {
                    case "spi_role":
                        if (!RoleKnown && !fetching_role)
                        {
                            fetching_role = true;
                            Utils.RunTask(FetchRole());
                        }
                        break;
                    case "spi_bounds":
                        if (!BoundsKnown && !watching_bounds)
                        {
                            watching_bounds = true;
                            Utils.RunTask(WatchBounds());
                        }
                        break;
                    case "spi_abs_pos":
                        if (!watching_abs_position)
                        {
                            watching_abs_position = true;
                            Utils.RunTask(RefreshAbsPos());
                        }
                        break;
                }
            }

            if (expression is BinaryExpression bin &&
                bin.Kind == GudlToken.Dot &&
                bin.Left is IdentifierExpression prop &&
                prop.Name == "spi_state")
            {
                if (!watching_states)
                {
                    watching_states = true;
                    Utils.RunTask(WatchStates());
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
                    case "spi_abs_pos":
                        if (watching_abs_position)
                        {
                            watching_abs_position = false;
                            if (abs_position_refresh_token != null)
                                abs_position_refresh_token.Cancel();
                        }
                        break;
                }
            }

            base.UnwatchProperty(expression);
        }

        private async Task RefreshAbsPos()
        {
            if (!watching_abs_position)
                return;

            (var x, var y) = await component.GetPositionAsync(0); // ATSPI_COORD_TYPE_SCREEN

            if (!watching_abs_position)
                return;

            if (!AbsPositionKnown || x != AbsX || y != AbsY)
            {
                AbsPositionKnown = true;
                AbsX = x;
                AbsY = y;
#if DEBUG
                Console.WriteLine($"{this}.spi_abs_pos: ({x},{y})");
#endif
                PropertyChanged("spi_abs_pos");
            }

            if (!watching_abs_position)
                return;

            abs_position_refresh_token = new CancellationTokenSource();

            try
            {
                await Task.Delay(500, abs_position_refresh_token.Token);
            }
            catch (TaskCanceledException)
            {
                abs_position_refresh_token = null;
                return;
            }

            abs_position_refresh_token = null;
            Utils.RunIdle(RefreshAbsPos()); // Unsure if RunTask would accumulate stack frames forever
        }

        private async Task WatchStates()
        {
            IDisposable state_changed_event = await object_events.WatchStateChangedAsync(OnStateChanged, Utils.OnError);
            if (this.state_changed_event != null)
                this.state_changed_event.Dispose();
            this.state_changed_event = state_changed_event;
            uint[] states = await acc.GetStateAsync();
            state.SetStates(states);
#if DEBUG
            Console.WriteLine($"{this}.spi_state: {state}");
#endif
        }

        private void OnStateChanged((string, uint, uint, object) obj)
        {
            string name = obj.Item1;
            bool value = obj.Item2 != 0;
            if (!AtSpiState.name_mapping.TryGetValue(name, out var actual_name) || actual_name != name)
            {
#if DEBUG
                Console.WriteLine($"unrecognized state {name} changed to {value} on {this}");
#endif
                return;
            }
            if (value)
            {
                if (!state.states.Add(name))
                    return;
            }
            else
            {
                if (!state.states.Remove(name))
                    return;
            }
            Console.WriteLine($"{this}.spi_state.{name}: {value}");
            PropertyChanged(new BinaryExpression(
                new IdentifierExpression("spi_state"),
                new IdentifierExpression(name),
                GudlToken.Dot
            ));
        }

        protected override UiDomValue EvaluateIdentifierCore(string id, UiDomRoot root, [In, Out] HashSet<(UiDomObject, GudlExpression)> depends_on)
        {
            switch (id)
            {
                case "spi_service":
                    // depends_on.Add((this, new IdentifierExpression(id))); // not needed because this property is known and can't change
                    return new UiDomString(Service);
                case "spi_path":
                    // depends_on.Add((this, new IdentifierExpression(id))); // not needed because this property is known and can't change
                    return new UiDomString(Path);
                case "role:":
                case "control_type:":
                case "controltype:":
                case "spi_role":
                    depends_on.Add((this, new IdentifierExpression("spi_role")));
                    if (Role > 0 && Role < role_to_enum.Length)
                        return role_to_enum[Role];
                    // TODO: return unknown values as numbers?
                    return UiDomUndefined.Instance;
                case "x_in_window":
                case "spi_x":
                    depends_on.Add((this, new IdentifierExpression("spi_bounds")));
                    if (BoundsKnown)
                        return new UiDomInt(X);
                    return UiDomUndefined.Instance;
                case "y_in_window":
                case "spi_y":
                    depends_on.Add((this, new IdentifierExpression("spi_bounds")));
                    if (BoundsKnown)
                        return new UiDomInt(Y);
                    return UiDomUndefined.Instance;
                case "width":
                case "spi_width":
                    depends_on.Add((this, new IdentifierExpression("spi_bounds")));
                    if (BoundsKnown)
                        return new UiDomInt(Width);
                    return UiDomUndefined.Instance;
                case "height":
                case "spi_height":
                    depends_on.Add((this, new IdentifierExpression("spi_bounds")));
                    if (BoundsKnown)
                        return new UiDomInt(Height);
                    return UiDomUndefined.Instance;
                case "bounds":
                case "spi_bounds":
                    depends_on.Add((this, new IdentifierExpression("spi_bounds")));
                    if (BoundsKnown)
                        return new UiDomString($"({X},{Y}: {Width}x{Height})");
                    return UiDomUndefined.Instance;
                case "spi_state":
                    return state;
                case "spi_abs_x":
                    depends_on.Add((this, new IdentifierExpression("spi_abs_pos")));
                    if (AbsPositionKnown)
                        return new UiDomInt(AbsX);
                    return UiDomUndefined.Instance;
                case "spi_abs_y":
                    depends_on.Add((this, new IdentifierExpression("spi_abs_pos")));
                    if (AbsPositionKnown)
                        return new UiDomInt(AbsY);
                    return UiDomUndefined.Instance;
                case "spi_abs_pos":
                    depends_on.Add((this, new IdentifierExpression("spi_abs_pos")));
                    if (AbsPositionKnown)
                        return new UiDomString($"({AbsX}, {AbsY})");
                    return UiDomUndefined.Instance;
                case "x":
                case "abs_x":
                    return Evaluate(abs_x_expr, depends_on);
                case "y":
                case "abs_y":
                    return Evaluate(abs_y_expr, depends_on);
            }

            if (name_to_role.TryGetValue(id, out var expected_role))
            {
                depends_on.Add((this, new IdentifierExpression("spi_role")));
                return UiDomBoolean.FromBool(Role == expected_role);
            }

            if (state.TryEvaluateIdentifier(id, depends_on, out var result))
            {
                return result;
            }

            return base.EvaluateIdentifierCore(id, root, depends_on);
        }
    }
}
