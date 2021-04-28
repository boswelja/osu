﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Performance;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;

namespace osu.Game.Rulesets.UI
{
    public class HitObjectContainer : CompositeDrawable, IHitObjectContainer
    {
        public IEnumerable<DrawableHitObject> Objects => InternalChildren.Cast<DrawableHitObject>().OrderBy(h => h.HitObject.StartTime);

        public IEnumerable<DrawableHitObject> AliveObjects => AliveInternalChildren.Cast<DrawableHitObject>().OrderBy(h => h.HitObject.StartTime);

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> is judged.
        /// </summary>
        public event Action<DrawableHitObject, JudgementResult> NewResult;

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> judgement is reverted.
        /// </summary>
        public event Action<DrawableHitObject, JudgementResult> RevertResult;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes used by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become alive.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageBegan;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes unused by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become dead.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageFinished;

        /// <summary>
        /// The amount of time prior to the current time within which <see cref="HitObject"/>s should be considered alive.
        /// </summary>
        internal double PastLifetimeExtension { get; set; }

        /// <summary>
        /// The amount of time after the current time within which <see cref="HitObject"/>s should be considered alive.
        /// </summary>
        internal double FutureLifetimeExtension { get; set; }

        private readonly Dictionary<DrawableHitObject, IBindable> startTimeMap = new Dictionary<DrawableHitObject, IBindable>();
        private readonly Dictionary<HitObjectLifetimeEntry, DrawableHitObject> drawableMap = new Dictionary<HitObjectLifetimeEntry, DrawableHitObject>();
        private readonly LifetimeEntryManager lifetimeManager = new LifetimeEntryManager();
        private readonly Dictionary<HitObjectLifetimeEntry, DrawableHitObject> nonPooledDrawableMap = new Dictionary<HitObjectLifetimeEntry, DrawableHitObject>();

        [Resolved(CanBeNull = true)]
        private IPooledHitObjectProvider pooledObjectProvider { get; set; }

        public HitObjectContainer()
        {
            RelativeSizeAxes = Axes.Both;

            lifetimeManager.EntryBecameAlive += entryBecameAlive;
            lifetimeManager.EntryBecameDead += entryBecameDead;
            lifetimeManager.EntryCrossedBoundary += entryCrossedBoundary;
        }

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();

            // Application of hitobjects during load() may have changed their start times, so ensure the correct sorting order.
            SortInternal();
        }

        #region Pooling support

        public void Add(HitObjectLifetimeEntry entry) => lifetimeManager.AddEntry(entry);

        public bool Remove(HitObjectLifetimeEntry entry)
        {
            if (!lifetimeManager.RemoveEntry(entry)) return false;
            // It has to be done here because non-pooled entry may be removed by specifying its entry.
            nonPooledDrawableMap.Remove(entry);
            return true;
        }

        private void entryBecameAlive(LifetimeEntry entry) => addDrawable((HitObjectLifetimeEntry)entry);

        private void entryBecameDead(LifetimeEntry entry) => removeDrawable((HitObjectLifetimeEntry)entry);

        private void addDrawable(HitObjectLifetimeEntry entry)
        {
            Debug.Assert(!drawableMap.ContainsKey(entry));

            nonPooledDrawableMap.TryGetValue(entry, out var drawable);
            drawable ??= pooledObjectProvider?.GetPooledDrawableRepresentation(entry.HitObject, null);
            if (drawable == null)
                throw new InvalidOperationException($"A drawable representation could not be retrieved for hitobject type: {entry.HitObject.GetType().ReadableName()}.");

            drawable.OnNewResult += onNewResult;
            drawable.OnRevertResult += onRevertResult;

            bindStartTime(drawable);
            AddInternal(drawableMap[entry] = drawable);
            OnAdd(drawable);

            HitObjectUsageBegan?.Invoke(entry.HitObject);
        }

        private void removeDrawable(HitObjectLifetimeEntry entry)
        {
            Debug.Assert(drawableMap.ContainsKey(entry));

            var drawable = drawableMap[entry];

            // OnKilled can potentially change the hitobject's result, so it needs to run first before unbinding.
            drawable.OnKilled();
            drawable.OnNewResult -= onNewResult;
            drawable.OnRevertResult -= onRevertResult;

            drawableMap.Remove(entry);

            OnRemove(drawable);
            unbindStartTime(drawable);
            RemoveInternal(drawable);

            // The hit object is not freed when the DHO was not pooled.
            if (!nonPooledDrawableMap.ContainsKey(entry))
                HitObjectUsageFinished?.Invoke(entry.HitObject);
        }

        #endregion

        #region Non-pooling support

        public virtual void Add(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                throw new InvalidOperationException($"May not add a {nameof(DrawableHitObject)} without {nameof(HitObject)} associated");

            nonPooledDrawableMap.Add(drawable.Entry, drawable);
            Add(drawable.Entry);
        }

        public virtual bool Remove(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                return false;

            return Remove(drawable.Entry);
        }

        public int IndexOf(DrawableHitObject hitObject) => IndexOfInternal(hitObject);

        private void entryCrossedBoundary(LifetimeEntry entry, LifetimeBoundaryKind kind, LifetimeBoundaryCrossingDirection direction)
        {
            if (nonPooledDrawableMap.TryGetValue((HitObjectLifetimeEntry)entry, out var drawable))
                OnChildLifetimeBoundaryCrossed(new LifetimeBoundaryCrossedEvent(drawable, kind, direction));
        }

        protected virtual void OnChildLifetimeBoundaryCrossed(LifetimeBoundaryCrossedEvent e)
        {
        }

        #endregion

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> is added to this container.
        /// </summary>
        /// <remarks>
        /// This method is not invoked for nested <see cref="DrawableHitObject"/>s.
        /// </remarks>
        protected virtual void OnAdd(DrawableHitObject drawableHitObject)
        {
        }

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> is removed from this container.
        /// </summary>
        /// <remarks>
        /// This method is not invoked for nested <see cref="DrawableHitObject"/>s.
        /// </remarks>
        protected virtual void OnRemove(DrawableHitObject drawableHitObject)
        {
        }

        public virtual void Clear()
        {
            lifetimeManager.ClearEntries();
            nonPooledDrawableMap.Clear();
            Debug.Assert(InternalChildren.Count == 0 && startTimeMap.Count == 0 && drawableMap.Count == 0, "All hit objects should have been removed");
        }

        protected override bool CheckChildrenLife()
        {
            bool aliveChanged = base.CheckChildrenLife();
            aliveChanged |= lifetimeManager.Update(Time.Current - PastLifetimeExtension, Time.Current + FutureLifetimeExtension);
            return aliveChanged;
        }

        private void onNewResult(DrawableHitObject d, JudgementResult r) => NewResult?.Invoke(d, r);
        private void onRevertResult(DrawableHitObject d, JudgementResult r) => RevertResult?.Invoke(d, r);

        #region Comparator + StartTime tracking

        private void bindStartTime(DrawableHitObject hitObject)
        {
            var bindable = hitObject.StartTimeBindable.GetBoundCopy();

            bindable.BindValueChanged(_ =>
            {
                if (LoadState >= LoadState.Ready)
                    SortInternal();
            });

            startTimeMap[hitObject] = bindable;
        }

        private void unbindStartTime(DrawableHitObject hitObject)
        {
            startTimeMap[hitObject].UnbindAll();
            startTimeMap.Remove(hitObject);
        }

        private void unbindAllStartTimes()
        {
            foreach (var kvp in startTimeMap)
                kvp.Value.UnbindAll();
            startTimeMap.Clear();
        }

        protected override int Compare(Drawable x, Drawable y)
        {
            if (!(x is DrawableHitObject xObj) || !(y is DrawableHitObject yObj))
                return base.Compare(x, y);

            // Put earlier hitobjects towards the end of the list, so they handle input first
            int i = yObj.HitObject.StartTime.CompareTo(xObj.HitObject.StartTime);
            return i == 0 ? CompareReverseChildID(x, y) : i;
        }

        #endregion

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            unbindAllStartTimes();
        }
    }
}
