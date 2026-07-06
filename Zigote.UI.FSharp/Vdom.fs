namespace Zigote.UI.FSharp

open System
open System.Collections.Generic
open Zigote.UI.Widgets

/// A single diffable property assignment on a retained widget. Between two renders attrs are
/// compared by (Name, Value); Apply runs only when the value changed. Function-typed values never
/// compare equal, so event handlers are refreshed on every render — required, because they close
/// over the current model.
[<ReferenceEquality>]
type Attr =
    {
        Name: string
        Value: obj
        Apply: Widget -> obj -> unit
        /// Restores the property default when the attr disappears between two renders of the same
        /// widget. None = the last applied value sticks (fine for attrs that are never conditional).
        Unset: (Widget -> unit) option
    }

/// What a view puts inside its widget: nothing, one child slot, or an ordered child list
/// (the widget must derive from MultiChildWidget).
[<ReferenceEquality; RequireQualifiedAccess>]
type Children =
    | None
    | One of View option
    | Many of View list

/// An immutable, cheap description of one retained widget: how to create it, how to configure it,
/// and what its children look like. View values are rebuilt on every render; the reconciler diffs
/// them and mutates the long-lived widget instances — so transient widget state (hover, focus,
/// caret, scroll, in-flight animations) survives across renders, per the retained model.
and [<ReferenceEquality>] View =
    {
        Kind: string
        Key: string option
        Create: unit -> Widget
        Attrs: Attr list
        Children: Children
        /// Installs/uninstalls the single child on this widget kind (used with Children.One).
        SetChild: (Widget -> Widget option -> unit) option
    }

/// The retained association between a View description and the live widget it produced.
/// `Nodes` mirrors the view's children (0/1 entries for Children.One).
[<ReferenceEquality>]
type Node =
    { View: View
      Widget: Widget
      Nodes: Node list }

/// Diffs a freshly-built View tree against the previous one and patches the retained widgets in
/// place: reused nodes get an attribute diff, structural changes create/detach subtrees. Child
/// lists reconcile by key first (stable identity across reorders), then by position for unkeyed
/// entries; MultiChildWidget.SetChildren handles attach/detach of the delta.
[<RequireQualifiedAccess>]
module Reconcile =

    /// Two views describe the same retained widget when kind and key agree.
    let canReuse (oldView: View) (newView: View) =
        oldView.Kind = newView.Kind && oldView.Key = newView.Key

    let private applyAll (w: Widget) (attrs: Attr list) =
        for a in attrs do
            a.Apply w a.Value

    /// Returns true when at least one attr was (re)applied or unset.
    let private patchAttrs (w: Widget) (oldAttrs: Attr list) (newAttrs: Attr list) =
        let mutable dirty = false

        for a in newAttrs do
            let prev = oldAttrs |> List.tryFind (fun o -> o.Name = a.Name)

            let unchanged =
                match prev with
                | Some o -> Object.Equals(o.Value, a.Value)
                | None -> false

            if not unchanged then
                a.Apply w a.Value
                dirty <- true

        for o in oldAttrs do
            if newAttrs |> List.forall (fun a -> a.Name <> o.Name) then
                match o.Unset with
                | Some reset ->
                    reset w
                    dirty <- true
                | None -> ()

        dirty

    let private setChild (view: View) (w: Widget) (child: Widget option) =
        match view.SetChild with
        | Some set -> set w child
        | None -> invalidOp $"view '%s{view.Kind}' does not accept a single child"

    let private attachIfLive (parent: Widget) (child: Widget) =
        match parent.Owner with
        | null -> ()
        | owner -> child.Attach(owner, parent)

    /// True when the container already holds exactly these instances in this order — lets patch
    /// skip SetChildren (and its relayout) when a render changed nothing structural.
    let private sameChildren (m: MultiChildWidget) (widgets: Widget list) =
        m.Children.Count = List.length widgets
        && List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) (List.ofSeq m.Children) widgets

    /// Build a fresh widget subtree for a view (no diffing).
    let rec create (view: View) : Node =
        let w = view.Create()
        applyAll w view.Attrs

        let nodes =
            match view.Children with
            | Children.None -> []
            | Children.One None -> []
            | Children.One(Some childView) ->
                let child = create childView
                setChild view w (Some child.Widget)
                [ child ]
            | Children.Many views ->
                let nodes = views |> List.map create

                match w with
                | :? MultiChildWidget as m -> m.SetChildren(nodes |> List.map (fun n -> n.Widget))
                | _ -> invalidOp $"view '%s{view.Kind}' does not accept a child list"

                nodes

        { View = view
          Widget = w
          Nodes = nodes }

    /// Patch a retained node with a new view of the same (kind, key). Recurses into children.
    and patch (node: Node) (newView: View) : Node =
        let w = node.Widget
        let mutable dirty = patchAttrs w node.View.Attrs newView.Attrs

        let nodes =
            match newView.Children with
            | Children.None -> []
            | Children.One newChild ->
                let oldChild = List.tryHead node.Nodes

                match oldChild, newChild with
                | None, None -> []
                | Some oc, Some cv when canReuse oc.View cv -> [ patch oc cv ]
                | Some oc, Some cv ->
                    oc.Widget.Detach()
                    let fresh = create cv
                    setChild newView w (Some fresh.Widget)
                    attachIfLive w fresh.Widget
                    dirty <- true
                    [ fresh ]
                | Some oc, None ->
                    oc.Widget.Detach()
                    setChild newView w None
                    dirty <- true
                    []
                | None, Some cv ->
                    let fresh = create cv
                    setChild newView w (Some fresh.Widget)
                    attachIfLive w fresh.Widget
                    dirty <- true
                    [ fresh ]
            | Children.Many newViews ->
                let nodes = reconcileMany node.Nodes newViews

                match w with
                | :? MultiChildWidget as m ->
                    let widgets = nodes |> List.map (fun n -> n.Widget)

                    if not (sameChildren m widgets) then
                        m.SetChildren widgets
                        dirty <- true
                | _ -> invalidOp $"view '%s{newView.Kind}' does not accept a child list"

                nodes

        if dirty then
            w.MarkNeedsLayout()

        { View = newView
          Widget = w
          Nodes = nodes }

    /// Keyed-then-positional list reconciliation. Keyed views reclaim their old node wherever it
    /// moved; unkeyed views consume the remaining old nodes in order when the kind matches.
    /// Dropped widgets are detached by the caller's SetChildren.
    and reconcileMany (oldNodes: Node list) (newViews: View list) : Node list =
        let keyed = Dictionary<string, Node>()
        let unkeyed = Queue<Node>()

        for n in oldNodes do
            match n.View.Key with
            | Some k ->
                if not (keyed.ContainsKey k) then
                    keyed[k] <- n
            | None -> unkeyed.Enqueue n

        let claimed = HashSet<Node>(HashIdentity.Reference)

        newViews
        |> List.map (fun view ->
            match view.Key with
            | Some k ->
                match keyed.TryGetValue k with
                | true, n when canReuse n.View view && claimed.Add n -> patch n view
                | _ -> create view
            | None ->
                if unkeyed.Count > 0 then
                    let n = unkeyed.Dequeue()

                    if canReuse n.View view && claimed.Add n then
                        patch n view
                    else
                        create view
                else
                    create view)

/// A StatelessWidget whose Build delegates to an injected function. Backs `Ui.contextual` —
/// reassigning Builder invalidates, so a context-dependent subtree re-runs on every render.
type FuncStatelessWidget(build: BuildContext -> Widget) =
    inherit StatelessWidget()

    let mutable build = build

    member this.Builder
        with get () = build
        and set value =
            build <- value
            this.Invalidate()

    override _.Build(ctx) = build ctx
