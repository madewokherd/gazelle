﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xalia.Gudl;

namespace Xalia.UiDom
{
    public class UiDomElement : UiDomValue
    {
        public string DebugId { get; }

        public List<UiDomElement> Children { get; } = new List<UiDomElement> ();

        private IUiDomProvider recurse_method_provider;
        public int RecurseMethodChildCount { get; private set; }

        public UiDomElement Parent { get; private set; }

        public bool IsAlive { get; private set; }

        public UiDomRoot Root { get; }

        private int cached_index_in_parent;

        public IReadOnlyCollection<string> Declarations => _activeDeclarations.Keys;

        public List<IUiDomProvider> Providers { get; private set; } = new List<IUiDomProvider>();

        private Dictionary<string, (GudlDeclaration, UiDomValue)> _activeDeclarations = new Dictionary<string, (GudlDeclaration, UiDomValue)>();

        private Dictionary<string, UiDomValue> _assignedProperties = new Dictionary<string, UiDomValue>();

        private Dictionary<GudlExpression, LinkedList<PropertyChangeNotifier>> _propertyChangeNotifiers = new Dictionary<GudlExpression, LinkedList<PropertyChangeNotifier>>();

        private Dictionary<(UiDomElement, GudlExpression), IDisposable> _dependencyPropertyChangeNotifiers = new Dictionary<(UiDomElement, GudlExpression), IDisposable>();

        bool _updatingRules;

        private Dictionary<GudlExpression, UiDomRelationshipWatcher> _relationshipWatchers = new Dictionary<GudlExpression, UiDomRelationshipWatcher>();
        private bool disposing;

        private Dictionary<GudlExpression, bool> polling_properties = new Dictionary<GudlExpression, bool>();
        private Dictionary<GudlExpression, CancellationTokenSource> polling_refresh_tokens = new Dictionary<GudlExpression, CancellationTokenSource>();

        private LinkedList<string[]> tracked_property_lists = new LinkedList<string[]>();
        private bool updating_tracked_properties;
        private Dictionary<string, UiDomValue> tracked_property_values = new Dictionary<string, UiDomValue>();
        private Dictionary<(UiDomElement, GudlExpression), IDisposable> tracked_property_notifiers = new Dictionary<(UiDomElement, GudlExpression), IDisposable>();

        private bool QueueEvaluateRules()
        {
            if (!IsAlive)
                return false;
            bool was_updating = _updatingRules;
            if (!was_updating)
            {
                _updatingRules = true;
                Utils.RunIdle(EvaluateRules);
            }
            return !was_updating;
        }

        protected virtual void SetAlive(bool value)
        {
            if (IsAlive != value)
            {
                IsAlive = value;
                if (value)
                {
                    foreach (var provider in Root.GlobalProviders)
                    {
                        var tracked = provider.GetTrackedProperties();
                        if (!(tracked is null))
                            RegisterTrackedProperties(tracked);
                    }
                    QueueEvaluateRules();
                }
                else
                {
                    foreach (var provider in Providers)
                    {
                        provider.NotifyElementRemoved(this);
                    }
                    foreach (var provider in Root.GlobalProviders)
                    {
                        provider.NotifyElementRemoved(this);
                    }
                    disposing = true;
                    foreach (var watcher in _relationshipWatchers.Values)
                    {
                        watcher.Dispose();
                    }
                    _relationshipWatchers.Clear();
                    foreach (var depNotifier in _dependencyPropertyChangeNotifiers.Values)
                    {
                        depNotifier.Dispose();
                    }
                    _dependencyPropertyChangeNotifiers.Clear();
                    foreach (var token in polling_refresh_tokens)
                    {
                        if (!(token.Value is null))
                            token.Value.Cancel(); 
                    }
                    polling_refresh_tokens.Clear();
                    polling_properties.Clear();
                    _updatingRules = false;
                    while (RecurseMethodChildCount < Children.Count)
                    {
                        RemoveChild(Children.Count - 1, false);
                    }
                    while (Children.Count != 0)
                    {
                        RemoveChild(Children.Count - 1, true);
                    }
                    Root?.RaiseElementDiedEvent(this);
                }
            }
        }

        public UiDomElement(string debug_id, UiDomRoot root)
        {
            DebugId = debug_id;
            Root = root;
        }

        internal UiDomElement()
        {
            if (this is UiDomRoot root)
            {
                Root = root;
                DebugId = "root";
                SetAlive(true);
            }
            else
                throw new InvalidOperationException("UiDomObject constructor with no arguments can only be used by UiDomRoot");
        }

        public void AddChild(int index, UiDomElement child, bool recurse_method=false)
        {
            if (index < 0 || index > Children.Count)
                throw new IndexOutOfRangeException();
            if (recurse_method)
            {
                if (index > RecurseMethodChildCount)
                    throw new IndexOutOfRangeException("index is too high to be used with recurse_method=true");
            }
            else
            {
                if (index < RecurseMethodChildCount)
                    throw new IndexOutOfRangeException("index is too low to be used with recurse_method=false");
            }
            if (MatchesDebugCondition())
                Utils.DebugWriteLine($"Child {child} added to {this} at index {index}");
            if (child.Parent != null)
                throw new InvalidOperationException(string.Format("Attempted to add child {0} to {1} but it already has a parent of {2}", child.DebugId, DebugId, child.Parent.DebugId));
            child.Parent = this;
            Children.Insert(index, child);
            child.cached_index_in_parent = index;
            if (recurse_method)
                RecurseMethodChildCount++;
            child.SetAlive(true);
            PropertyChanged("children");
        }

        internal void RelationshipValueChanged(UiDomRelationshipWatcher watcher)
        {
            PropertyChanged(watcher.AsProperty);
        }

        internal UiDomValue EvaluateRelationship(UiDomRelationshipKind kind, GudlExpression expr)
        {
            if (_relationshipWatchers.TryGetValue(
                new ApplyExpression(
                    new IdentifierExpression(UiDomRelationship.NameFromKind(kind)),
                    new GudlExpression[] { expr }),
                out var watcher))
            {
                return watcher.Value;
            }
            return UiDomUndefined.Instance;
        }

        public void RemoveChild(int index, bool recurse_method = false)
        {
            if (index < 0 || index >= Children.Count)
                throw new IndexOutOfRangeException();
            if (recurse_method)
            {
                if (index > RecurseMethodChildCount - 1)
                    throw new IndexOutOfRangeException("index too high to be used with recurse_method=true");
            }
            else
            {
                if (index < RecurseMethodChildCount)
                    throw new IndexOutOfRangeException("index too low to be used with recurse_method=false");
            }
            var child = Children[index];
            if (MatchesDebugCondition() || child.MatchesDebugCondition())
                Utils.DebugWriteLine($"Child {child} removed from {this}");
            Children.RemoveAt(index);
            child.Parent = null;
            child.SetAlive(false);
            if (recurse_method)
                RecurseMethodChildCount--;
            if (IsAlive)
                PropertyChanged("children");
        }

        public override string ToString()
        {
            return DebugId;
        }

        public UiDomValue Evaluate(GudlExpression expr, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            return Evaluate(expr, Root, depends_on);
        }

        public void AssignProperty(string propName, UiDomValue propValue)
        {
            if (propValue is UiDomUndefined)
            {
                if (_assignedProperties.ContainsKey(propName))
                {
                    if (MatchesDebugCondition())
                        Utils.DebugWriteLine($"{this}.{propName} assigned: {propValue}");
                    _assignedProperties.Remove(propName);
                    PropertyChanged(new IdentifierExpression(propName));
                    return;
                }
            }

            if (!_assignedProperties.TryGetValue(propName, out var oldValue) || !oldValue.Equals(propValue))
            {
                if (MatchesDebugCondition())
                    Utils.DebugWriteLine($"{this}.{propName} assigned: {propValue}");
                _assignedProperties[propName] = propValue;
                PropertyChanged(propName);
                return;
            }
        }

        public UiDomValue GetDeclaration(string property)
        {
            if (_activeDeclarations.TryGetValue(property, out var decl) && !(decl.Item2 is UiDomUndefined))
                return decl.Item2;
            if (_assignedProperties.TryGetValue(property, out var result) && !(result is UiDomUndefined))
                return result;
            return UiDomUndefined.Instance;
        }

        protected override UiDomValue EvaluateIdentifierCore(string id, UiDomRoot root, [In, Out] HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            UiDomValue value;
            foreach (var provider in Providers)
            {
                value = provider.EvaluateIdentifier(this, id, depends_on);
                if (!(value is UiDomUndefined))
                    return value;
            }
            foreach (var provider in Root.GlobalProviders)
            {
                value = provider.EvaluateIdentifier(this, id, depends_on);
                if (!(value is UiDomUndefined))
                    return value;
            }
            switch (id)
            {
                case "this":
                    return this;
                case "element_identifier":
                    return new UiDomString(DebugId);
                case "this_or_ancestor_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.ThisOrAncestor);
                case "this_or_descendent_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.ThisOrDescendent);
                case "ancestor_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.Ancestor);
                case "descendent_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.Descendent);
                case "child_matches":
                case "first_child_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.Child);
                case "parent_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.Parent);
                case "last_child_matches":
                    return new UiDomRelationship(this, UiDomRelationshipKind.LastChild);
                case "sibling_matches":
                case "first_sibling_matches":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        return new UiDomRelationship(Parent, UiDomRelationshipKind.Child);
                    }
                case "last_sibling_matches":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        return new UiDomRelationship(Parent, UiDomRelationshipKind.LastChild);
                    }
                case "next_sibling_matches":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        return new UiDomRelationship(this, UiDomRelationshipKind.NextSibling);
                    }
                case "previous_sibling_matches":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        return new UiDomRelationship(this, UiDomRelationshipKind.PreviousSibling);
                    }
                case "first_child":
                    {
                        depends_on.Add((this, new IdentifierExpression("children")));
                        if (Children.Count == 0)
                            return UiDomUndefined.Instance;
                        return Children[0];
                    }
                case "last_child":
                    {
                        depends_on.Add((this, new IdentifierExpression("children")));
                        if (Children.Count == 0)
                            return UiDomUndefined.Instance;
                        return Children[Children.Count - 1];
                    }
                case "first_sibling":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        depends_on.Add((Parent, new IdentifierExpression("children")));
                        return Parent.Children[0];
                    }
                case "last_sibling":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        depends_on.Add((Parent, new IdentifierExpression("children")));
                        return Parent.Children[Parent.Children.Count - 1];
                    }
                case "next_sibling":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        depends_on.Add((Parent, new IdentifierExpression("children")));
                        int idx = Parent.Children.IndexOf(this) + 1;
                        if (idx < Parent.Children.Count)
                            return Parent.Children[idx];
                        return UiDomUndefined.Instance;
                    }
                case "previous_sibling":
                    {
                        if (Parent is null)
                            return UiDomUndefined.Instance;
                        depends_on.Add((Parent, new IdentifierExpression("children")));
                        int idx = Parent.Children.IndexOf(this) - 1;
                        if (idx >= 0)
                            return Parent.Children[idx];
                        return UiDomUndefined.Instance;
                    }
                case "parent":
                    // We assume for now that this cannot change during an object's lifetime
                    return (UiDomValue)Parent ?? UiDomUndefined.Instance;
                case "is_child_of":
                    return new UiDomIsRelationship(this, UiDomIsRelationship.IsRelationshipType.Child);
                case "is_parent_of":
                    return new UiDomIsRelationship(this, UiDomIsRelationship.IsRelationshipType.Parent);
                case "is_ancestor_of":
                    return new UiDomIsRelationship(this, UiDomIsRelationship.IsRelationshipType.Ancestor);
                case "is_descendent_of":
                    return new UiDomIsRelationship(this, UiDomIsRelationship.IsRelationshipType.Descendent);
                case "is_sibling_of":
                    return new UiDomIsRelationship(this, UiDomIsRelationship.IsRelationshipType.Sibling);
                case "is_root":
                    return UiDomBoolean.FromBool(this is UiDomRoot);
                case "root":
                    return root;
                case "assign":
                    return new UiDomMethod(this, "assign", AssignFn);
                case "index_in_parent":
                    if (Parent is null)
                        return UiDomUndefined.Instance;
                    depends_on.Add((Parent, new IdentifierExpression("children")));
                    return new UiDomInt(IndexInParent);
                case "child_at_index":
                    return new UiDomMethod(this, "child_at_index", ChildAtIndexFn);
                case "child_count":
                    depends_on.Add((this, new IdentifierExpression("children")));
                    return new UiDomInt(Children.Count);
                case "repeat_action":
                    return UiDomRepeatAction.GetMethod();
                case "do_action":
                    return UiDomDoAction.Instance;
                case "map_directions":
                    return new UiDomMethod("map_directions", UiDomMapDirections.ApplyFn);
                case "adjust_scrollbars":
                    return new UiDomMethod("adjust_scrollbars", AdjustScrollbarsMethod);
                case "adjust_value":
                    return new UiDomMethod("adjust_value", AdjustValueMethod);
                case "radial_deadzone":
                    return new UiDomMethod("radial_deadzone", UiDomRadialDeadzone.ApplyFn);
                case "on_release":
                    return new UiDomMethod("on_release", OnReleaseMethod);
                case "wait":
                    return new UiDomMethod("wait", WaitMethod);
                case "enum":
                    return new UiDomMethod("enum", EnumMethod);
                case "hex":
                    return new UiDomMethod("hex", HexMethod);
                case "environ":
                    return UiDomEnviron.Instance;
            }
            var result = root.Application.EvaluateIdentifierHook(this, id, depends_on);
            if (!(result is null))
            {
                return result;
            }
            depends_on.Add((this, new IdentifierExpression(id)));
            if (_activeDeclarations.TryGetValue(id, out var decl) && !(decl.Item2 is UiDomUndefined))
                return decl.Item2;
            if (_assignedProperties.TryGetValue(id, out result) && !(result is UiDomUndefined))
                return result;
            foreach (var provider in Providers)
            {
                value = provider.EvaluateIdentifierLate(this, id, depends_on);
                if (!(value is UiDomUndefined))
                    return value;
            }
            foreach (var provider in Root.GlobalProviders)
            {
                value = provider.EvaluateIdentifierLate(this, id, depends_on);
                if (!(value is UiDomUndefined))
                    return value;
            }
            return UiDomUndefined.Instance;
        }

        private UiDomValue WaitMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length != 1)
                return UiDomUndefined.Instance;

            var val = context.Evaluate(arglist[0], root, depends_on);

            if (!val.TryToDouble(out var timeout))
                return UiDomUndefined.Instance;

            return new UiDomRoutineAsync("wait", new UiDomValue[] { val }, WaitRoutine);
        }

        private async Task WaitRoutine(UiDomRoutineAsync obj)
        {
            obj.Arglist[0].TryToDouble(out double timeout);
            await Task.Delay((int)(timeout * 1000));
        }

        private UiDomValue HexMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length != 1)
                return UiDomUndefined.Instance;

            var val = context.Evaluate(arglist[0], root, depends_on);

            if (!(val is UiDomInt i))
                return UiDomUndefined.Instance;

            return new UiDomString($"0x{i.Value.ToString("x", CultureInfo.InvariantCulture)}");
        }

        private UiDomValue EnumMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length < 1)
                return UiDomUndefined.Instance;

            string[] args = new string[arglist.Length];

            for (int i = 0; i < args.Length; i++)
            {
                var val = context.Evaluate(arglist[i], root, depends_on);
                if (!(val is UiDomString str))
                    return UiDomUndefined.Instance;
                args[i] = str.Value;
            }

            return new UiDomEnum(args);
        }

        private UiDomValue OnReleaseMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length < 1)
                return UiDomUndefined.Instance;
            var routine = context.Evaluate(arglist[0], Root, depends_on) as UiDomRoutine;
            if (routine is null)
                return UiDomUndefined.Instance;
            return new UiDomOnRelease(routine);
        }

        private UiDomValue AdjustValueMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length < 1)
                return UiDomUndefined.Instance;
            if (!Evaluate(arglist[0], depends_on).TryToDouble(out var offset))
                return UiDomUndefined.Instance;
            return new UiDomRoutineAsync(this, "adjust_value", new UiDomValue[] {new UiDomDouble(offset)}, AdjustValueRoutine);
        }

        private static async Task AdjustValueRoutine(UiDomRoutineAsync obj)
        {
            await obj.Element.OffsetValue(((UiDomDouble)obj.Arglist[0]).Value);
        }

        private UiDomValue ChildAtIndexFn(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length != 1)
                return UiDomUndefined.Instance;
            var expr = arglist[0];
            UiDomValue right = context.Evaluate(expr, root, depends_on);
            if (right.TryToInt(out int i))
            {
                depends_on.Add((this, new IdentifierExpression("children")));
                if (i >= 0 && i < Children.Count)
                {
                    return Children[i];
                }
            }
            return UiDomUndefined.Instance;
        }

        private UiDomValue AssignFn(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length != 2)
                return UiDomUndefined.Instance;

            var name = context.Evaluate(arglist[0], root, depends_on);

            if (!(name is UiDomString st))
            {
                return UiDomUndefined.Instance;
            }

            var value = context.Evaluate(arglist[1], root, depends_on);

            UiDomValue[] values = new UiDomValue[] { name, value };

            return new UiDomRoutineSync(this, "assign", values, DoAssign);
        }

        private static void DoAssign(UiDomRoutineSync obj)
        {
            obj.Element.AssignProperty(((UiDomString)obj.Arglist[0]).Value, obj.Arglist[1]);
        }

        public virtual async Task<(bool, int, int)> GetClickablePoint()
        {
            int x, y;
            if (GetDeclaration("target_x").TryToInt(out int tx) &&
                GetDeclaration("target_y").TryToInt(out int ty) &&
                GetDeclaration("target_width").TryToInt(out int tw) &&
                GetDeclaration("target_height").TryToInt(out int th))
            {
                x = tx + tw / 2;
                y = ty + th / 2;
                return (true, x, y);
            }
            foreach (var provider in Providers)
            {
                var result = await provider.GetClickablePointAsync(this);
                if (result.Item1)
                    return result;
            }
            foreach (var provider in Root.GlobalProviders)
            {
                var result = await provider.GetClickablePointAsync(this);
                if (result.Item1)
                    return result;
            }
            return (false, 0, 0);
        }

        static GudlExpression debugCondition;

        public bool MatchesDebugCondition()
        {
            if (debugCondition is null)
            {
                string conditionStr = Environment.GetEnvironmentVariable("XALIA_DEBUG");
                if (conditionStr is null)
                {
                    debugCondition = new IdentifierExpression("false");
                }
                else
                {
                    debugCondition = GudlParser.ParseExpression(conditionStr);
                }
            }
            return Evaluate(debugCondition, new HashSet<(UiDomElement, GudlExpression)>()).ToBool();
        }

        private void EvaluateRules()
        {
            _updatingRules = false;
            if (!IsAlive)
                return;
            var activeDeclarations = new Dictionary<string, (GudlDeclaration, UiDomValue)>();
            bool stop = false;
            var depends_on = new HashSet<(UiDomElement, GudlExpression)>();
            foreach ((GudlExpression expr, GudlDeclaration[] declarations) in Root.Rules)
            {
                bool any_new_declarations = false;
                foreach (var decl in declarations)
                {
                    if (decl.Property == "stop" || !activeDeclarations.ContainsKey(decl.Property))
                    {
                        any_new_declarations = true;
                        break;
                    }
                }
                if (!any_new_declarations)
                    continue;

                if (!(expr is null))
                {
                    UiDomValue condition = Evaluate(expr, Root, depends_on);

                    if (!condition.ToBool())
                        continue;
                }

                foreach (var decl in declarations)
                {
                    if (activeDeclarations.ContainsKey(decl.Property) && decl.Property != "stop")
                    {
                        continue;
                    }

                    UiDomValue value = Evaluate(decl.Value, depends_on);

                    if (decl.Property == "stop" && value.ToBool())
                        stop = true;

                    activeDeclarations[decl.Property] = (decl, value);
                }

                if (stop)
                    break;
            }

            DeclarationsChanged(activeDeclarations, depends_on);

            Root?.RaiseElementDeclarationsChangedEvent(this);

            if (MatchesDebugCondition())
            {
                Utils.DebugWriteLine($"properties for {DebugId}:");
                DumpProperties();
            }
        }

        protected virtual void DumpProperties()
        {
            foreach (var provider in Providers)
            {
                provider.DumpProperties(this);
            }
            foreach (var provider in Root.GlobalProviders)
            {
                provider.DumpProperties(this);
            }
            if (!(Parent is null))
            {
                Utils.DebugWriteLine($"  parent: {Parent.DebugId}");
                Utils.DebugWriteLine($"  index_in_parent: {IndexInParent}");
            }
            for (int i = 0; i < Children.Count; i++)
            {
                Utils.DebugWriteLine($"  child_at_index({i}): {Children[i].DebugId}");
            }
            foreach (var kvp in _relationshipWatchers)
            {
                if (!(kvp.Value.Value is UiDomUndefined))
                {
                    Utils.DebugWriteLine($"  {kvp.Key}: {kvp.Value.Value}");
                }
            }
            Root.Application.DumpElementProperties(this);
            foreach (var kvp in _activeDeclarations)
            {
                if (!(kvp.Value.Item2 is UiDomUndefined))
                {
                    Utils.DebugWriteLine($"  {kvp.Key}: {kvp.Value.Item2} [{kvp.Value.Item1.Position}]");
                }
            }
            foreach (var kvp in _assignedProperties)
            {
                if (!(kvp.Value is UiDomUndefined))
                {
                    Utils.DebugWriteLine($"  {kvp.Key}: {kvp.Value} [assigned]");
                }
            }
        }

        private void DeclarationsChanged(Dictionary<string, (GudlDeclaration, UiDomValue)> all_declarations,
            HashSet<(UiDomElement, GudlExpression)> dependencies)
        {
            HashSet<GudlExpression> changed = new HashSet<GudlExpression>();

            if (disposing)
            {
                return;
            }

            foreach (var kvp in _activeDeclarations)
            {
                if (!all_declarations.TryGetValue(kvp.Key, out var value) || !value.Equals(kvp.Value))
                    changed.Add(new IdentifierExpression(kvp.Key));
            }

            foreach (var key in all_declarations.Keys)
            {
                if (!_activeDeclarations.ContainsKey(key))
                    changed.Add(new IdentifierExpression(key));
            }

            _activeDeclarations = all_declarations;

            var updated_dependency_notifiers = new Dictionary<(UiDomElement, GudlExpression), IDisposable>();

            foreach (var dep in dependencies)
            {
                if (_dependencyPropertyChangeNotifiers.TryGetValue(dep, out var notifier))
                {
                    updated_dependency_notifiers[dep] = notifier;
                    _dependencyPropertyChangeNotifiers.Remove(dep);
                }
                else
                {
                    updated_dependency_notifiers.Add(dep,
                        dep.Item1.NotifyPropertyChanged(dep.Item2, OnDependencyPropertyChanged));
                }
            }
            foreach (var notifier in _dependencyPropertyChangeNotifiers.Values)
            {
                notifier.Dispose();
            }
            _dependencyPropertyChangeNotifiers = updated_dependency_notifiers;

            PropertiesChanged(changed);
        }

        private void OnDependencyPropertyChanged(UiDomElement element, GudlExpression property)
        {
            if (QueueEvaluateRules() && MatchesDebugCondition())
                Utils.DebugWriteLine($"queued rule evaluation for {this} because {element}.{property} changed");
        }

        public delegate void PropertyChangeHandler(UiDomElement element, GudlExpression property);

        private class PropertyChangeNotifier : IDisposable
        {
            public PropertyChangeNotifier(UiDomElement element, GudlExpression expression, PropertyChangeHandler handler)
            {
                Element = element;
                Expression = expression;
                Handler = handler;
                Element.AddPropertyChangeNotifier(this);
            }

            public readonly UiDomElement Element;
            public readonly GudlExpression Expression;
            public readonly PropertyChangeHandler Handler;
            bool Disposed;

            public void Dispose()
            {
                if (!Disposed)
                {
                    Element.RemovePropertyChangeNotifier(this);
                }
                Disposed = true;
            }
        }

        private void RemovePropertyChangeNotifier(PropertyChangeNotifier propertyChangeNotifier)
        {
            var expr = propertyChangeNotifier.Expression;
            var notifiers = _propertyChangeNotifiers[expr];

            if (notifiers.Count == 1)
            {
                _propertyChangeNotifiers.Remove(expr);
                UnwatchProperty(expr);
                return;
            }

            notifiers.Remove(propertyChangeNotifier);
        }

        private void AddPropertyChangeNotifier(PropertyChangeNotifier propertyChangeNotifier)
        {
            var expr = propertyChangeNotifier.Expression;
            if (_propertyChangeNotifiers.TryGetValue(expr, out var notifiers))
            {
                notifiers.AddLast(propertyChangeNotifier);
            }
            else
            {
                _propertyChangeNotifiers.Add(expr,
                    new LinkedList<PropertyChangeNotifier>(new PropertyChangeNotifier[] { propertyChangeNotifier }));

                WatchProperty(expr);
            }
        }

        public IDisposable NotifyPropertyChanged(GudlExpression expression, PropertyChangeHandler handler)
        {
            return new PropertyChangeNotifier(this, expression, handler);
        }

        protected virtual void WatchProperty(GudlExpression expression)
        {
            if (disposing)
            {
                return;
            }
            foreach (var provider in Providers)
            {
                if (provider.WatchProperty(this, expression))
                    return;
            }
            foreach (var provider in Root.GlobalProviders)
            {
                if (provider.WatchProperty(this, expression))
                    return;
            }
            if (expression is ApplyExpression apply)
            {
                if (apply.Left is IdentifierExpression prop &&
                    apply.Arglist.Length == 1)
                {
                    if (UiDomRelationship.Names.TryGetValue(prop.Name, out var kind))
                    {
                        _relationshipWatchers.Add(expression,
                            new UiDomRelationshipWatcher(this, kind, apply.Arglist[0]));
                    }
                }
            }
        }

        protected virtual void UnwatchProperty(GudlExpression expression)
        {
            if (disposing)
            {
                return;
            }
            foreach (var provider in Providers)
            {
                if (provider.UnwatchProperty(this, expression))
                    return;
            }
            foreach (var provider in Root.GlobalProviders)
            {
                if (provider.UnwatchProperty(this, expression))
                    return;
            }
            if (_relationshipWatchers.TryGetValue(expression, out var watcher))
            {
                watcher.Dispose();
                _relationshipWatchers.Remove(expression);
            }
        }

        protected internal void PropertyChanged(string identifier)
        {
            PropertyChanged(new IdentifierExpression(identifier));
        }

        protected internal void PropertyChanged(string identifier, object val)
        {
            if (MatchesDebugCondition())
                Utils.DebugWriteLine($"{this}.{identifier}: {val}");
            PropertyChanged(identifier);
        }

        protected internal void PropertyChanged(GudlExpression property)
        {
            HashSet<GudlExpression> properties = new HashSet<GudlExpression> { property };
            PropertiesChanged(properties);
        }

        protected virtual void PropertiesChanged(HashSet<GudlExpression> changed_properties)
        {
            if (!IsAlive)
                return;
            foreach (var prop in changed_properties)
            {
                bool modified = true;
                while (modified) {
                    modified = false;
                    if (_propertyChangeNotifiers.TryGetValue(prop, out var notifiers))
                    {
                        var e = notifiers.GetEnumerator();
                        while (true)
                        {
                            bool next;
                            try
                            {
                                next = e.MoveNext();
                            }
                            catch (InvalidOperationException)
                            {
                                // Collection was modified
                                modified = true;
                                break;
                            }
                            if (!next)
                                break;
                            e.Current.Handler(this, prop);
                        }
                    }
                }
            }
        }

        private static UiDomValue AdjustScrollbarsMethod(UiDomMethod method, UiDomValue context, GudlExpression[] arglist, UiDomRoot root, [In, Out] HashSet<(UiDomElement, GudlExpression)> depends_on)
        {
            if (arglist.Length >= 2)
            {
                var hscroll = context.Evaluate(arglist[0], root, depends_on) as UiDomElement;
                var vscroll = context.Evaluate(arglist[1], root, depends_on) as UiDomElement;

                if (hscroll is null && vscroll is null)
                    return UiDomUndefined.Instance;

                return new UiDomAdjustScrollbars(hscroll, vscroll);
            }
            return UiDomUndefined.Instance;
        }

        public virtual async Task<double> GetMinimumIncrement()
        {
            foreach (var provider in Providers)
            {
                if (provider is IUiDomValueProvider value)
                {
                    var result = await value.GetMinimumIncrementAsync(this);
                    if (result != 0)
                        return result;
                }
            }
            foreach (var provider in Root.GlobalProviders)
            {
                if (provider is IUiDomValueProvider value)
                {
                    var result = await value.GetMinimumIncrementAsync(this);
                    if (result != 0)
                        return result;
                }
            }
            return 25.0;
        }

        public virtual async Task OffsetValue(double ofs)
        {
            foreach (var provider in Providers)
            {
                if (provider is IUiDomValueProvider value)
                {
                    if (await value.OffsetValueAsync(this, ofs))
                        return;
                }
            }
            foreach (var provider in Root.GlobalProviders)
            {
                if (provider is IUiDomValueProvider value)
                {
                    if (await value.OffsetValueAsync(this, ofs))
                        return;
                }
            }
        }

        public void PollProperty(GudlExpression expression, Func<Task> refresh_function, int default_interval)
        {
            if (!polling_properties.TryGetValue(expression, out var polling) || !polling)
            {
                polling_properties[expression] = true;
                Utils.RunTask(DoPollProperty(expression, refresh_function, default_interval));
            }
        }

        private async Task DoPollProperty(GudlExpression expression, Func<Task> refresh_function, int default_interval)
        {
            if (!polling_properties.TryGetValue(expression, out bool polling) || !polling)
                return;

            await refresh_function();

            if (!polling_properties.TryGetValue(expression, out polling) || !polling)
                return;

            var token = new CancellationTokenSource();

            polling_refresh_tokens[expression] = token;

            try
            {
                await Task.Delay(default_interval);
            }
            catch (TaskCanceledException)
            {
                polling_refresh_tokens[expression] = null;
                return;
            }

            polling_refresh_tokens[expression] = null;
            Utils.RunTask(DoPollProperty(expression, refresh_function, default_interval));
        }

        public void EndPollProperty(GudlExpression expression)
        {
            if (polling_properties.TryGetValue(expression, out var polling) && polling)
            {
                polling_properties[expression] = false;
                if (polling_refresh_tokens.TryGetValue(expression, out var token) && !(token is null))
                {
                    token.Cancel();
                    polling_refresh_tokens[expression] = null;
                }
            }
        }

        private void OnTrackedDependencyChanged(UiDomElement element, GudlExpression property)
        {
            if (!updating_tracked_properties)
            {
                updating_tracked_properties = true;
                Utils.RunIdle(UpdateTrackedProperties);
            }
        }

        protected void RegisterTrackedProperties(string[] properties)
        {
            tracked_property_lists.AddLast(properties);
            if (!updating_tracked_properties)
            {
                updating_tracked_properties = true;
                Utils.RunIdle(UpdateTrackedProperties);
            }
        }

        private void UpdateTrackedProperties()
        {
            updating_tracked_properties = false;

            Dictionary<string, UiDomValue> new_property_values = new Dictionary<string, UiDomValue>();
            HashSet<(UiDomElement, GudlExpression)> depends_on = new HashSet<(UiDomElement, GudlExpression)>();

            // Evaluate all tracked properties
            foreach (var proplist in tracked_property_lists)
            {
                foreach (var propname in proplist)
                {
                    if (new_property_values.ContainsKey(propname))
                        continue;
                    var propvalue = EvaluateIdentifier(propname, Root, depends_on);
                    new_property_values[propname] = propvalue;
                }
            }

            // Update dependency notifiers
            var updated_dependency_notifiers = new Dictionary<(UiDomElement, GudlExpression), IDisposable>();
            foreach (var dep in depends_on)
            {
                if (tracked_property_notifiers.TryGetValue(dep, out var notifier))
                {
                    updated_dependency_notifiers[dep] = notifier;
                    tracked_property_notifiers.Remove(dep);
                }
                else
                {
                    updated_dependency_notifiers.Add(dep,
                        dep.Item1.NotifyPropertyChanged(dep.Item2, OnTrackedDependencyChanged));
                }
            }
            foreach (var notifier in tracked_property_notifiers.Values)
            {
                notifier.Dispose();
            }
            tracked_property_notifiers = updated_dependency_notifiers;

            // Notify subclass of updates
            var old_values = tracked_property_values;
            tracked_property_values = new_property_values;
            foreach (var kvp in new_property_values)
            {
                if (!old_values.TryGetValue(kvp.Key, out var old_value) || !kvp.Value.Equals(old_value))
                {
                    TrackedPropertyChanged(kvp.Key, kvp.Value);
                }
            }
        }

        protected virtual void TrackedPropertyChanged(string name, UiDomValue new_value)
        {
            foreach (var provider in Providers)
            {
                provider.TrackedPropertyChanged(this, name, new_value);
            }
            foreach (var provider in Root.GlobalProviders)
            {
                provider.TrackedPropertyChanged(this, name, new_value);
            }
        }

        private void ProviderAdded(IUiDomProvider provider)
        {
            var tracked = provider.GetTrackedProperties();
            if (!(tracked is null))
                RegisterTrackedProperties(tracked);
            if (QueueEvaluateRules() && MatchesDebugCondition())
                Utils.DebugWriteLine($"queued rule evaluation for {this} because {provider} was added");
            foreach (var expression in _propertyChangeNotifiers.Keys)
            {
                provider.WatchProperty(this, expression);
            }
        }

        public void AddProvider(IUiDomProvider provider, int index)
        {
            Providers.Insert(index, provider);
            ProviderAdded(provider);
        }

        public void AddProvider(IUiDomProvider provider)
        {
            AddProvider(provider, Providers.Count);
        }

        internal void AddedGlobalProvider(IUiDomProvider provider)
        {
            ProviderAdded(provider);
            foreach (var child in Children)
            {
                child.AddedGlobalProvider(provider);
            }
        }

        public T ProviderByType<T>() where T : IUiDomProvider
        {
            foreach (var provider in Providers)
            {
                if (provider is T result)
                    return result;
            }
            return default;
        }

        public void SetRecurseMethodProvider(IUiDomProvider provider)
        {
            recurse_method_provider = provider;
        }

        public void UnsetRecurseMethodProvider(IUiDomProvider provider)
        {
            if (recurse_method_provider == provider)
            {
                recurse_method_provider = null;
                Utils.RunIdle(ClearRecurseMethodChildren);
            }
        }

        private void ClearRecurseMethodChildren()
        {
            if (recurse_method_provider is null)
            {
                SyncRecurseMethodChildren(new int[] { }, throw_nie_string, throw_nie_element);
            }
        }

        private static UiDomElement throw_nie_element(int arg)
        {
            throw new NotImplementedException();
        }

        private static string throw_nie_string(int arg)
        {
            throw new NotImplementedException();
        }

        public void SyncRecurseMethodChildren<T>(IList<T> keys, Func<T,string> key_to_id,
            Func<T,UiDomElement> key_to_element)
        {
            if (!IsAlive)
                return;

            var new_ids = new string[keys.Count];
            var new_elements = new UiDomElement[keys.Count];
            var new_id_to_index = new Dictionary<string, int>(keys.Count);
            bool changed = false;

            for (int i=0; i<keys.Count; i++)
            {
                new_ids[i] = key_to_id(keys[i]);
                if (new_ids[i] is null)
                    continue;
                try
                {
                    new_id_to_index.Add(new_ids[i], i);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException("keys list has a duplicate entry");
                }
            }

            var old_elements = new UiDomElement[RecurseMethodChildCount];
            Children.CopyTo(0, old_elements, 0, RecurseMethodChildCount);

            for (int i=0; i<old_elements.Length; i++)
            {
                if (new_id_to_index.TryGetValue(old_elements[i].DebugId, out var new_index))
                {
                    if (new_index != i)
                        changed = true;
                    new_elements[new_index] = old_elements[i];
                    old_elements[i] = null;
                }
                else
                {
                    // element will be removed
                    changed = true;
                    if (MatchesDebugCondition() || old_elements[i].MatchesDebugCondition())
                        Utils.DebugWriteLine($"Child {old_elements[i]} removed from {this}");
                }
            }

            if (!changed && keys.Count == RecurseMethodChildCount)
            {
                // All existing elements have same index, and the count hasn't increased, therefore nothing changed.
                return;
            }

            for (int i=0; i<new_elements.Length; i++)
            {
                if (new_elements[i] is null)
                {
                    new_elements[i] = key_to_element(keys[i]);
                    if (MatchesDebugCondition())
                        Utils.DebugWriteLine($"Child {new_ids[i]} added to {this} at index {i}");
                }
            }

            // Adjust the length of the list, and shift the other elements
            if (keys.Count > RecurseMethodChildCount)
            {
                Children.InsertRange(RecurseMethodChildCount,
                    new UiDomElement[keys.Count - RecurseMethodChildCount]);
            }
            else if (keys.Count < RecurseMethodChildCount)
            {
                Children.RemoveRange(keys.Count, RecurseMethodChildCount - keys.Count);
            }
            RecurseMethodChildCount = keys.Count;

            // Update the list to ensure we're in a consistent state when we start calling user code
            for (int i = 0; i < new_elements.Length; i++)
                Children[i] = new_elements[i];

            // Update parent links and alive state
            foreach (var child in new_elements)
            {
                if (!child.IsAlive)
                {
                    child.Parent = this;
                    child.SetAlive(true);
                }
            }

            foreach (var old_child in old_elements)
            {
                if (!(old_child is null))
                {
                    old_child.Parent = null;
                    old_child.SetAlive(false);
                }
            }

            if (MatchesDebugCondition())
                Utils.DebugWriteLine($"children of {this} changed");
            PropertyChanged("children");
        }

        public int IndexInParent
        {
            get
            {
                if (Parent is null)
                    return -1;
                if (Parent.Children.Count <= cached_index_in_parent ||
                    Parent.Children[cached_index_in_parent] != this)
                {
                    Parent.RecalculateChildIndices();
                }
                return cached_index_in_parent;
            }
        }

        private void RecalculateChildIndices()
        {
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].cached_index_in_parent = i;
            }
        }
    }
}
