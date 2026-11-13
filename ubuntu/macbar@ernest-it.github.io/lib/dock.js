// The dock: Apps button, pinned apps, a divider and running apps, centered at the bottom of the primary
// monitor. Running dots, hover magnification, launch bounce, drag to reorder (or drag apps in from the app
// grid), right-click menu, and macOS-style auto-hide or Ubuntu-style intellihide.
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as BoxPointer from 'resource:///org/gnome/shell/ui/boxpointer.js';
import * as DND from 'resource:///org/gnome/shell/ui/dnd.js';
import * as AppFavorites from 'resource:///org/gnome/shell/ui/appFavorites.js';

import {timeout, removeSource} from './util.js';

const GAP = 6; // space between the dock and the screen edge

function appWindows(app) {
    // Windows on the current workspace first (MRU order from Shell), then the rest.
    const ws = global.workspace_manager.get_active_workspace();
    const wins = app.get_windows().filter(w => !w.skip_taskbar);
    return wins.filter(w => w.get_workspace() === ws).concat(wins.filter(w => w.get_workspace() !== ws));
}

const DockItem = GObject.registerClass(
class DockItem extends St.Button {
    _init(dock, app, kind) {
        super._init({
            style_class: 'macbar-dock-item', reactive: true, can_focus: true, track_hover: true,
            button_mask: St.ButtonMask.ONE | St.ButtonMask.TWO,
        });
        this._dock = dock;
        this.app = app;           // null for the Apps button
        this.kind = kind;         // 'apps', 'pinned', 'running'
        this._delegate = this;

        const size = dock.iconSize;
        const box = new St.BoxLayout({vertical: true});
        this.iconActor = kind === 'apps'
            ? new St.Bin({style_class: 'macbar-apps-icon', width: size, height: size,
                child: new St.Icon({icon_name: 'view-app-grid-symbolic', icon_size: Math.round(size * 0.55)})})
            : app.create_icon_texture(size);
        this.iconActor.set_pivot_point(0.5, 1);
        this._dot = new St.Widget({style_class: 'macbar-dock-dot', x_align: Clutter.ActorAlign.CENTER});
        box.add_child(this.iconActor);
        box.add_child(this._dot);
        this.set_child(box);
        this.syncRunning();

        this.connect('clicked', (b, button) => this._onClick(button));
        this.connect('button-press-event', (a, ev) => {
            if (ev.get_button() === 3) { this._openMenu(); return Clutter.EVENT_STOP; }
            return Clutter.EVENT_PROPAGATE;
        });
        this.connect('notify::hover', () => dock.onHover(this));
        this.connect('popup-menu', () => this._openMenu());

        if (app) {
            const tip = app.get_name();
            this.accessible_name = tip;
            this._draggable = DND.makeDraggable(this);
            this._draggable.connect('drag-begin', () => { dock.dragging = true; this.iconActor.opacity = 80; });
            this._draggable.connect('drag-end', () => { dock.dragging = false; this.iconActor.opacity = 255; });
        } else {
            this.accessible_name = 'Apps';
        }
    }

    // DND source support
    getDragActor() { return this.app.create_icon_texture(this._dock.iconSize); }
    getDragActorSource() { return this.iconActor; }

    syncRunning() {
        const running = this.app && this.app.state === Shell.AppState.RUNNING && this.app.get_n_windows() > 0;
        this._dot.opacity = running ? 255 : 0;
    }

    bounce() {
        this.iconActor.remove_transition?.('translation-y');
        const up = () => this.iconActor.ease({
            translation_y: -14, duration: 160, mode: Clutter.AnimationMode.EASE_OUT_QUAD,
            onComplete: () => this.iconActor.ease({translation_y: 0, duration: 200, mode: Clutter.AnimationMode.EASE_IN_QUAD}),
        });
        up();
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 380, () => { if (this.iconActor) up(); return GLib.SOURCE_REMOVE; });
    }

    _onClick(button) {
        this._dock.closeMenus();
        if (this.kind === 'apps') {
            if (Main.overview.visible && Main.overview.dash.showAppsButton.checked) Main.overview.hide();
            else Main.overview.showApps();
            return;
        }
        const app = this.app;
        if (button === 2) { // middle click: new window
            this.bounce();
            app.open_new_window(-1);
            return;
        }
        Main.overview.hide();
        const wins = appWindows(app);
        if (wins.length === 0) {
            this.bounce();
            if (app.state === Shell.AppState.STOPPED) app.activate();
            else app.open_new_window(-1);
            return;
        }
        const focus = global.display.focus_window;
        if (focus && wins.includes(focus)) {
            if (wins.length === 1) { focus.minimize(); return; }
            // Several windows: cycle through them in a stable order.
            const sorted = wins.slice().sort((a, b) => a.get_stable_sequence() - b.get_stable_sequence());
            const next = sorted[(sorted.indexOf(focus) + 1) % sorted.length];
            Main.activateWindow(next);
            return;
        }
        // Bring all of the app's windows on this workspace forward, most recent on top.
        const here = wins.filter(w => w.get_workspace() === global.workspace_manager.get_active_workspace());
        if (here.length > 1) for (const w of here.slice(1).reverse()) if (!w.minimized) w.raise();
        Main.activateWindow(wins[0]);
    }

    _openMenu() {
        if (this.kind === 'apps') return;
        this._dock.closeMenus();
        const app = this.app;
        const menu = new PopupMenu.PopupMenu(this, 0.5, St.Side.BOTTOM);
        menu.actor.add_style_class_name('macbar-dock-menu');
        Main.uiGroup.add_child(menu.actor);
        this._dock.menuManager.addMenu(menu);

        const wins = appWindows(app);
        if (wins.length) {
            for (const w of wins) {
                const it = new PopupMenu.PopupMenuItem(w.get_title() || app.get_name());
                it.connect('activate', () => Main.activateWindow(w));
                menu.addMenuItem(it);
            }
            menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        }
        if (app.can_open_new_window() || app.state === Shell.AppState.STOPPED) {
            const it = new PopupMenu.PopupMenuItem('New Window');
            it.connect('activate', () => { this.bounce(); app.open_new_window(-1); });
            menu.addMenuItem(it);
        }
        // Desktop actions (e.g. "New Incognito Window").
        const info = app.get_app_info();
        if (info) {
            for (const action of info.list_actions()) {
                const it = new PopupMenu.PopupMenuItem(info.get_action_name(action));
                it.connect('activate', () => app.launch_action(action, global.get_current_time(), -1));
                menu.addMenuItem(it);
            }
        }
        const favs = AppFavorites.getAppFavorites();
        const isFav = favs.isFavorite(app.get_id());
        const pin = new PopupMenu.PopupMenuItem(isFav ? 'Remove from Dock' : 'Keep in Dock');
        pin.connect('activate', () => {
            if (isFav) favs.removeFavorite(app.get_id()); else favs.addFavorite(app.get_id());
        });
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addMenuItem(pin);
        if (app.state !== Shell.AppState.STOPPED) {
            const q = new PopupMenu.PopupMenuItem(wins.length > 1 ? `Quit (${wins.length} windows)` : 'Quit');
            q.connect('activate', () => app.request_quit());
            menu.addMenuItem(q);
        }
        menu.connect('open-state-changed', (m, open) => {
            this._dock.menuOpen = open;
            if (!open) {
                // Destroy after the close animation.
                GLib.idle_add(GLib.PRIORITY_DEFAULT, () => { menu.destroy(); return GLib.SOURCE_REMOVE; });
            }
        });
        menu.open(BoxPointer.PopupAnimation.FULL);
    }
});

export class Dock {
    constructor(ext) {
        this._settings = ext.getSettings();
        this.dragging = false;
        this.menuOpen = false;
        this._items = [];
        this._shown = true;
        this._lastWanted = Date.now();

        // Full-width strip along the bottom (only it may reserve space), with the dock centered inside.
        this.container = new St.Widget({name: 'macbarDockContainer', layout_manager: new Clutter.BinLayout(), reactive: false});
        this.box = new St.BoxLayout({name: 'macbarDock', style_class: 'macbar-dock', reactive: true, track_hover: true,
            x_align: Clutter.ActorAlign.CENTER, y_align: Clutter.ActorAlign.START});
        this.box._delegate = this;
        this.container.add_child(this.box);
        this.menuManager = new PopupMenu.PopupMenuManager(this.box);

        this._applyLook();
        this._addChrome();
        this._rebuild();

        this._appSystem = Shell.AppSystem.get_default();
        this._favorites = AppFavorites.getAppFavorites();
        this._signals = [
            [this._favorites, this._favorites.connect('changed', () => this._queueRebuild())],
            [this._appSystem, this._appSystem.connect('app-state-changed', () => this._queueRebuild())],
            [this._appSystem, this._appSystem.connect('installed-changed', () => this._queueRebuild())],
            [global.display, global.display.connect('window-created', () => this._queueRebuild())],
            [global.workspace_manager, global.workspace_manager.connect('active-workspace-changed', () => this._syncDots())],
            [Main.layoutManager, Main.layoutManager.connect('monitors-changed', () => this._reposition())],
            [Main.overview, Main.overview.connect('showing', () => this._setShown(true, true))],
            [Main.overview, Main.overview.connect('hidden', () => this._tick())],
            [this.box, this.box.connect('notify::width', () => this._reposition())],
            [this.box, this.box.connect('notify::height', () => this._reposition())],
        ];
        this._settingIds = [
            this._settings.connect('changed::dock-icon-size', () => this._rebuild()),
            this._settings.connect('changed::dock-autohide', () => { this._addChrome(); this._tick(); }),
            this._settings.connect('changed::show-dock', () => this._syncVisible()),
            this._settings.connect('changed::bar-opacity', () => this._applyLook()),
            this._settings.connect('changed::blur', () => this._applyLook()),
        ];
        this._overviewDash(true);
        this._syncVisible();
        this._timer = timeout(100, () => { this._tick(); return true; });
    }

    get iconSize() { return this._settings.get_int('dock-icon-size'); }
    get _mode() { return this._settings.get_string('dock-autohide'); }

    _addChrome() {
        if (this._chromeAdded) Main.layoutManager.removeChrome(this.container);
        // Reserve space only when the dock never hides, so maximized windows stop above it.
        Main.layoutManager.addChrome(this.container, {
            affectsStruts: this._mode === 'never', affectsInputRegion: false, trackFullscreen: true,
        });
        if (!this._boxTracked) {
            Main.layoutManager.trackChrome(this.box, {affectsInputRegion: true});
            this._boxTracked = true;
        }
        this._chromeAdded = true;
        this._reposition();
    }

    _syncVisible() {
        this.container.visible = this._settings.get_boolean('show-dock');
    }

    /** GNOME's own dash in the overview would sit under our dock, so hide it while we run. */
    _overviewDash(hide) {
        const dash = Main.overview.dash;
        if (!dash) return;
        if (hide) { dash.hide(); dash.set_height(0); }
        else { dash.set_height(-1); dash.show(); }
    }

    _applyLook() {
        const a = Math.max(0, Math.min(1, this._settings.get_double('bar-opacity')));
        // The dock is a little more solid than the menu bar so icons stay readable.
        this.box.set_style(`background-color: rgba(30, 30, 34, ${Math.min(1, a + 0.15).toFixed(2)});`);
        if (this._blur) { this.box.remove_effect(this._blur); this._blur = null; }
        const blur = this._settings.get_string('blur');
        if (blur !== 'none' && Shell.BlurEffect) {
            try {
                this._blur = new Shell.BlurEffect({mode: Shell.BlurMode.BACKGROUND, brightness: 0.9});
                const r = blur === 'strong' ? 40 : 15;
                if ('radius' in this._blur) this._blur.radius = r; else this._blur.sigma = r / 2;
                this.box.add_effect_with_name('macbar-blur', this._blur);
            } catch (e) { this._blur = null; }
        }
    }

    _queueRebuild() {
        if (this._rebuildId) return;
        this._rebuildId = GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE, () => {
            this._rebuildId = 0;
            this._rebuild();
            return GLib.SOURCE_REMOVE;
        });
    }

    _rebuild() {
        if (this.dragging) { this._queueRebuild(); return; }
        const favorites = AppFavorites.getAppFavorites().getFavorites();
        const favIds = new Set(favorites.map(a => a.get_id()));
        const running = Shell.AppSystem.get_default().get_running()
            .filter(a => !favIds.has(a.get_id()) && a.get_windows().some(w => !w.skip_taskbar));

        const wanted = [['apps', null], ...favorites.map(a => ['pinned', a])];
        if (running.length) wanted.push(['sep', null]);
        wanted.push(...running.map(a => ['running', a]));

        // Skip the rebuild when nothing changed (keeps hover state and animations smooth).
        const key = `${this.iconSize}|` + wanted.map(([k, a]) => `${k}:${a ? a.get_id() : ''}`).join(',');
        if (key === this._key) { this._syncDots(); return; }
        this._key = key;

        this.box.destroy_all_children();
        this._items = [];
        for (const [kind, app] of wanted) {
            if (kind === 'sep') {
                this.box.add_child(new St.Widget({style_class: 'macbar-dock-separator', y_expand: true}));
                continue;
            }
            const it = new DockItem(this, app, kind);
            this._items.push(it);
            this.box.add_child(it);
        }
        this._reposition();
    }

    _syncDots() {
        for (const it of this._items) it.syncRunning();
    }

    _reposition() {
        const m = Main.layoutManager.primaryMonitor;
        if (!m) return;
        const [, natH] = this.box.get_preferred_height(-1);
        const h = Math.round(natH) + GAP;
        this.container.set_position(m.x, m.y + m.height - h);
        this.container.set_size(m.width, h);
    }

    // ----- hover magnification -----

    onHover(item) {
        const mag = this._settings.get_double('dock-magnify');
        const hovered = this._items.find(i => i.hover);
        const idx = hovered ? this._items.indexOf(hovered) : -1;
        this._items.forEach((it, i) => {
            let s = 1;
            if (idx >= 0 && mag > 1) {
                const d = Math.abs(i - idx);
                s = d === 0 ? mag : d === 1 ? 1 + (mag - 1) * 0.45 : d === 2 ? 1 + (mag - 1) * 0.15 : 1;
            }
            it.iconActor.ease({scale_x: s, scale_y: s, duration: 120, mode: Clutter.AnimationMode.EASE_OUT_QUAD});
        });
    }

    closeMenus() {
        if (this.menuManager.activeMenu) this.menuManager.activeMenu.close(BoxPointer.PopupAnimation.NONE);
    }

    // ----- drag and drop (reorder pinned apps, or pin an app dragged in from the app grid) -----

    _dropIndex(x) {
        const pinned = this._items.filter(i => i.kind === 'pinned');
        let pos = 0;
        for (const it of pinned) {
            const [ix] = it.get_transformed_position();
            if (x > ix + it.width / 2) pos++;
        }
        return pos;
    }

    handleDragOver(source, actor, x, y) {
        const app = source && source.app;
        if (!app || app.is_window_backed?.()) return DND.DragMotionResult.NO_DROP;
        return AppFavorites.getAppFavorites().isFavorite(app.get_id()) ? DND.DragMotionResult.MOVE_DROP : DND.DragMotionResult.COPY_DROP;
    }

    acceptDrop(source, actor, x, y) {
        const app = source && source.app;
        if (!app || app.is_window_backed?.()) return false;
        const [bx] = this.box.get_transformed_position();
        const pos = this._dropIndex(bx + x);
        const favs = AppFavorites.getAppFavorites();
        const id = app.get_id();
        GLib.idle_add(GLib.PRIORITY_DEFAULT, () => {
            if (favs.isFavorite(id)) {
                const cur = favs.getFavorites().findIndex(a => a.get_id() === id);
                favs.moveFavoriteToPos(id, cur < pos ? pos - 1 : pos);
            } else {
                favs.addFavoriteAtPos(id, pos);
            }
            return GLib.SOURCE_REMOVE;
        });
        return true;
    }

    // ----- auto-hide -----

    _overlapsFocused() {
        const w = global.display.focus_window;
        if (!w || w.minimized || w.get_window_type() !== Meta.WindowType.NORMAL) return false;
        if (w.get_monitor() !== Main.layoutManager.primaryIndex) return false;
        const r = w.get_frame_rect();
        const [bx, by] = this.box.get_transformed_position();
        const bw = this.box.width, bh = this.box.height;
        const top = this._shown ? by : by - this.box.translation_y;
        return r.x < bx + bw && r.x + r.width > bx && r.y + r.height > top && r.y < top + bh;
    }

    _tick() {
        if (!this.container.visible) return;
        const mode = this._mode;
        const m = Main.layoutManager.primaryMonitor;
        if (!m) return;
        let want = true;
        if (mode !== 'never' && !Main.overview.visible) {
            const [x, y] = global.get_pointer();
            const atEdge = y >= m.y + m.height - 2 && x >= m.x && x < m.x + m.width;
            const [bx, by] = this.box.get_transformed_position();
            const overDock = this._shown && x >= bx - 12 && x <= bx + this.box.width + 12 && y >= by - 24;
            const busy = this.menuOpen || this.dragging;
            want = atEdge || overDock || busy;
            if (mode === 'intellihide' && !want) want = !this._overlapsFocused();
            if (want) this._lastWanted = Date.now();
            // Short delay before hiding so the dock doesn't flicker when the mouse brushes past.
            else if (Date.now() - this._lastWanted < (mode === 'intellihide' ? 150 : 450)) want = true;
        }
        this._setShown(want);
    }

    _setShown(shown, now = false) {
        if (shown === this._shown && !now) return;
        this._shown = shown;
        const h = this.box.height + GAP + 4;
        this.box.ease({
            translation_y: shown ? 0 : h, duration: now ? 0 : 180, mode: Clutter.AnimationMode.EASE_OUT_QUAD,
            onComplete: () => Main.layoutManager._queueUpdateRegions?.(),
        });
    }

    destroy() {
        this._timer = removeSource(this._timer);
        if (this._rebuildId) { GLib.source_remove(this._rebuildId); this._rebuildId = 0; }
        this.closeMenus();
        for (const [obj, id] of this._signals) obj.disconnect(id);
        this._settingIds.forEach(id => this._settings.disconnect(id));
        this._overviewDash(false);
        if (this._boxTracked) Main.layoutManager.untrackChrome(this.box);
        Main.layoutManager.removeChrome(this.container);
        this.container.destroy();
    }
}
