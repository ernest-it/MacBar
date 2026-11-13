// The menu bar: GNOME's top panel restyled and rearranged to work like the macOS menu bar.
// Left: system menu and the active app's name. Middle: Now Playing. Right: lighting, weather, sound,
// GNOME's quick settings (our Control Center) and the clock.
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as LoginManager from 'resource:///org/gnome/shell/misc/loginManager.js';

import {SHELL_MAJOR, icon, spawn, haveProgram, timeout, removeSource} from './util.js';
import {SoundIndicator} from './sound.js';
import {WeatherIndicator} from './weather.js';
import {LightingIndicator} from './lighting.js';
import {NowPlayingIndicator} from './nowplaying.js';

const SystemMenu = GObject.registerClass(
class SystemMenu extends PanelMenu.Button {
    _init(ext, workspaces) {
        super._init(0.0, 'MacBar', false);
        this._ext = ext;
        this._workspaces = workspaces;
        this.add_style_class_name('macbar-logo-button');
        this.add_child(new St.Icon({
            gicon: icon('ubuntu-logo-symbolic', 'distributor-logo-symbolic', 'start-here-symbolic', 'view-app-grid-symbolic'),
            style_class: 'system-status-icon macbar-logo',
        }));
        this.menu.connect('open-state-changed', (m, open) => { if (open) this._build(); });
        this._build();
    }

    _item(label, fn, menu = this.menu) {
        const it = new PopupMenu.PopupMenuItem(label);
        it.connect('activate', () => { Main.overview.hide(); fn(); });
        menu.addMenuItem(it);
        return it;
    }

    _build() {
        this.menu.removeAll();
        this._item('About This Computer', () => spawn(['gnome-control-center', SHELL_MAJOR >= 46 ? 'system' : 'info-overview']));
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._item('System Settings…', () => spawn(['gnome-control-center']));
        this._item('MacBar Settings…', () => this._ext.openPreferences());
        const store = ['snap-store_snap-store.desktop', 'snap-store_ubuntu-software.desktop', 'org.gnome.Software.desktop']
            .map(id => Shell.AppSystem.get_default().lookup_app(id)).find(a => a);
        if (store) this._item('App Center…', () => store.activate());

        // Workspaces: GNOME menus can't nest, so the submenu expands in place.
        const ws = new PopupMenu.PopupSubMenuMenuItem('Workspaces');
        for (const w of this._workspaces.list()) {
            const head = new PopupMenu.PopupMenuItem(w.name, {reactive: false, style_class: 'macbar-menu-heading'});
            ws.menu.addMenuItem(head);
            this._item('    Open Missing Apps & Arrange', () => this._workspaces.run(w, true), ws.menu);
            this._item('    Arrange Open Windows Only', () => this._workspaces.run(w, false), ws.menu);
        }
        if (this._workspaces.list().length) ws.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._item('Save Open Windows as New Workspace', () => this._workspaces.captureNew(), ws.menu);
        this._item('Edit Workspaces…', () => this._ext.openPreferences(), ws.menu);
        this.menu.addMenuItem(ws);

        if (haveProgram('gnome-system-monitor')) {
            this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
            this._item('Force Quit… (System Monitor)', () => spawn(['gnome-system-monitor']));
        }
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._item('Sleep', () => LoginManager.getLoginManager().suspend());
        this._item('Restart…', () => spawn(['gnome-session-quit', '--reboot']));
        this._item('Shut Down…', () => spawn(['gnome-session-quit', '--power-off']));
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._item('Lock Screen', () => { if (Main.screenShield) Main.screenShield.lock(true); else spawn(['loginctl', 'lock-session']); });
        this._item(`Log Out ${GLibUserName()}…`, () => spawn(['gnome-session-quit', '--logout']));
    }
});

function GLibUserName() {
    const real = GLib.get_real_name();
    return real && real !== 'Unknown' ? real : GLib.get_user_name();
}

/** The active app's name in bold, with a small menu (New Window, Quit) like macOS. */
const AppName = GObject.registerClass(
class AppName extends PanelMenu.Button {
    _init() {
        super._init(0.0, 'MacBar App Name', false);
        this.add_style_class_name('macbar-app-name-button');
        this._label = new St.Label({style_class: 'macbar-app-name', y_align: Clutter.ActorAlign.CENTER});
        this.add_child(this._label);
        this._tracker = Shell.WindowTracker.get_default();
        this._focusId = this._tracker.connect('notify::focus-app', () => this._update());
        this._overviewIds = [
            Main.overview.connect('showing', () => this._update()),
            Main.overview.connect('hidden', () => this._update()),
        ];
        this.menu.connect('open-state-changed', (m, open) => { if (open) this._build(); });
        this._update();
    }

    _update() {
        const app = Main.overview.visible ? null : this._tracker.focus_app;
        this._app = app;
        this._label.text = app ? app.get_name() : 'Desktop';
    }

    _build() {
        this.menu.removeAll();
        const app = this._app;
        if (!app) {
            const it = new PopupMenu.PopupMenuItem('Show Apps');
            it.connect('activate', () => Main.overview.showApps());
            this.menu.addMenuItem(it);
            return;
        }
        if (app.can_open_new_window()) {
            const nw = new PopupMenu.PopupMenuItem('New Window');
            nw.connect('activate', () => app.open_new_window(-1));
            this.menu.addMenuItem(nw);
        }
        const all = new PopupMenu.PopupMenuItem('Show All Windows');
        all.connect('activate', () => Main.overview.show());
        this.menu.addMenuItem(all);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const q = new PopupMenu.PopupMenuItem(`Quit ${app.get_name()}`);
        q.connect('activate', () => app.request_quit());
        this.menu.addMenuItem(q);
    }

    destroy() {
        this._tracker.disconnect(this._focusId);
        this._overviewIds.forEach(id => Main.overview.disconnect(id));
        super.destroy();
    }
});

export class TopBar {
    constructor(ext, workspaces) {
        this._ext = ext;
        this._settings = ext.getSettings();
        this._indicators = {};
        this._settingIds = [];

        Main.panel.add_style_class_name('macbar-panel');
        this._applyLook();

        // Activities button out (the dock's Apps button and the Super key cover it).
        const act = Main.panel.statusArea.activities;
        if (act) act.container.hide();

        this._add('macbar-system', new SystemMenu(ext, workspaces), 0, 'left');
        this._syncOptional();
        this._syncGnomeVolumeIcon();

        this._moveClock(this._settings.get_boolean('clock-on-right'));

        for (const k of ['bar-opacity'])
            this._settingIds.push(this._settings.connect(`changed::${k}`, () => this._applyLook()));
        for (const k of ['show-app-name', 'show-now-playing', 'show-weather', 'show-sound', 'show-lighting'])
            this._settingIds.push(this._settings.connect(`changed::${k}`, () => { this._syncOptional(); this._syncGnomeVolumeIcon(); }));
        this._settingIds.push(this._settings.connect('changed::clock-on-right', () => this._moveClock(this._settings.get_boolean('clock-on-right'))));

        this._setupPeek();
        this._setupSuperKey();

        // The logo menu sits where GNOME's hot corner is; moving to it shouldn't open the overview.
        this._iface = new Gio.Settings({schema_id: 'org.gnome.desktop.interface'});
        this._hadHotCorners = this._iface.get_boolean('enable-hot-corners');
        if (this._hadHotCorners) this._iface.set_boolean('enable-hot-corners', false);
    }

    _add(role, indicator, pos, box) {
        this._indicators[role] = indicator;
        Main.panel.addToStatusArea(role, indicator, pos, box);
    }

    _remove(role) {
        const ind = this._indicators[role];
        if (ind) { ind.destroy(); delete this._indicators[role]; }
    }

    _syncOptional() {
        const want = {
            'macbar-appname': this._settings.get_boolean('show-app-name'),
            'macbar-nowplaying': this._settings.get_boolean('show-now-playing'),
            'macbar-lighting': this._settings.get_boolean('show-lighting'),
            'macbar-weather': this._settings.get_boolean('show-weather'),
            'macbar-sound': this._settings.get_boolean('show-sound'),
        };
        for (const [role, on] of Object.entries(want)) {
            if (!on) { this._remove(role); continue; }
            if (this._indicators[role]) continue;
            switch (role) {
            case 'macbar-appname': this._add(role, new AppName(), 1, 'left'); break;
            case 'macbar-nowplaying': this._add(role, new NowPlayingIndicator(), 0, 'center'); break;
            // Right box, left to right: lighting, weather, sound, then GNOME's own icons.
            case 'macbar-lighting': this._add(role, new LightingIndicator(this._settings), 0, 'right'); break;
            case 'macbar-weather': this._add(role, new WeatherIndicator(this._ext, this._settings), this._indicators['macbar-lighting'] ? 1 : 0, 'right'); break;
            case 'macbar-sound': {
                const before = ['macbar-lighting', 'macbar-weather'].filter(r => this._indicators[r]).length;
                this._add(role, new SoundIndicator(), before, 'right');
                break;
            }
            }
        }
    }

    /** Our sound menu replaces GNOME's speaker icon in the quick settings (its slider stays inside the menu). */
    _syncGnomeVolumeIcon() {
        const hide = this._settings.get_boolean('show-sound');
        const ind = Main.panel.statusArea.quickSettings?._volumeOutput;
        if (!ind) {
            // Quick settings set up their indicators asynchronously; try again shortly.
            if (hide && !this._qsRetry) this._qsRetry = timeout(1000, () => { this._qsRetry = 0; this._syncGnomeVolumeIcon(); return false; });
            return;
        }
        if (hide && !this._qsVolId) {
            ind.hide();
            this._qsVol = ind;
            this._qsVolId = ind.connect('notify::visible', () => { if (ind.visible) ind.hide(); });
        } else if (!hide && this._qsVolId) {
            ind.disconnect(this._qsVolId);
            this._qsVolId = 0;
            ind.visible = ind._indicator ? ind._indicator.visible : true;
        }
    }

    /** Translucent tint; the wallpaper shows through. (No blur: a background blur behind the top panel paints black.) */
    _applyLook() {
        const a = Math.max(0, Math.min(1, this._settings.get_double('bar-opacity')));
        this._panelStyle = `background-color: rgba(20, 20, 24, ${a.toFixed(2)});`;
        Main.panel.set_style(this._panelStyle);
        // GNOME clears the panel's style during startup; put ours back whenever that happens.
        if (!this._styleId) {
            this._styleId = Main.panel.connect('notify::style', () => {
                if (this._panelStyle && Main.panel.get_style() !== this._panelStyle) Main.panel.set_style(this._panelStyle);
            });
        }
    }

    _moveClock(right) {
        const dm = Main.panel.statusArea.dateMenu;
        if (!dm) return;
        const c = dm.container;
        const parent = c.get_parent();
        if (parent) parent.remove_child(c);
        if (right) Main.panel._rightBox.add_child(c);
        else Main.panel._centerBox.insert_child_at_index(c, 0);
        this._clockRight = right;
    }

    // ----- Full screen: hold the mouse at the top edge to bring the menu bar down -----

    _setupPeek() {
        this._peeking = false;
        this._edgeSince = 0;
        this._awaySince = 0;
        this._fsId = global.display.connect('in-fullscreen-changed', () => this._syncPeekTimer());
        this._syncPeekTimer();
    }

    _primaryFullscreen() {
        const m = Main.layoutManager.primaryMonitor;
        return m && m.inFullscreen && !Main.overview.visible;
    }

    _syncPeekTimer() {
        const want = this._primaryFullscreen() && this._settings.get_boolean('peek-fullscreen');
        if (want && !this._peekTimer) {
            this._peekTimer = timeout(60, () => { this._peekTick(); return true; });
        } else if (!want && this._peekTimer) {
            this._peekTimer = removeSource(this._peekTimer);
            this._setPeek(false);
        }
    }

    _setPeek(on) {
        if (this._peeking === on) return;
        this._peeking = on;
        const box = Main.layoutManager.panelBox;
        if (on) {
            box.show();
            // Above the full-screen window.
            const parent = box.get_parent();
            if (parent) parent.set_child_above_sibling(box, null);
        } else {
            Main.layoutManager._updateVisibility?.();
        }
        Main.layoutManager._queueUpdateRegions?.();
        this._edgeSince = 0; this._awaySince = 0;
    }

    _peekTick() {
        if (!this._primaryFullscreen()) { this._setPeek(false); return; }
        const m = Main.layoutManager.primaryMonitor;
        const [x, y] = global.get_pointer();
        const now = Date.now();
        if (!this._peeking) {
            const atEdge = y <= m.y + 1 && x >= m.x && x < m.x + m.width;
            if (!atEdge) { this._edgeSince = 0; return; }
            if (!this._edgeSince) this._edgeSince = now;
            // A short hold so flicking past the edge mid-game doesn't drop the bar.
            if (now - this._edgeSince >= 250) this._setPeek(true);
            return;
        }
        const panelH = Main.panel.height + 6;
        const menuOpen = !!Main.panel.menuManager.activeMenu;
        const keep = menuOpen || (y < m.y + panelH && x >= m.x && x < m.x + m.width);
        if (keep) { this._awaySince = 0; return; }
        if (!this._awaySince) this._awaySince = now;
        if (now - this._awaySince >= 500) this._setPeek(false);
    }

    // ----- Super key opens the app grid -----

    _setupSuperKey() {
        // Main's handler toggles the overview first; ours runs after it. If that just opened the
        // overview, switch it to the app grid (if it just closed it, leave it closed).
        this._showingAt = 0;
        this._showingId = Main.overview.connect('showing', () => { this._showingAt = Date.now(); });
        this._overlayId = global.display.connect('overlay-key', () => {
            if (!this._settings.get_boolean('super-opens-apps')) return;
            if (Date.now() - this._showingAt < 150) Main.overview.showApps();
        });
    }

    destroy() {
        if (this._hadHotCorners) this._iface.set_boolean('enable-hot-corners', true);
        this._qsRetry = removeSource(this._qsRetry);
        if (this._qsVolId) {
            this._qsVol.disconnect(this._qsVolId);
            this._qsVolId = 0;
            this._qsVol.visible = this._qsVol._indicator ? this._qsVol._indicator.visible : true;
        }
        if (this._peekTimer) this._peekTimer = removeSource(this._peekTimer);
        if (this._peeking) this._setPeek(false);
        global.display.disconnect(this._fsId);
        global.display.disconnect(this._overlayId);
        Main.overview.disconnect(this._showingId);
        this._settingIds.forEach(id => this._settings.disconnect(id));
        for (const role of Object.keys(this._indicators)) this._remove(role);
        if (this._clockRight) this._moveClock(false);
        const act = Main.panel.statusArea.activities;
        if (act) act.container.show();
        if (this._styleId) { Main.panel.disconnect(this._styleId); this._styleId = 0; }
        this._panelStyle = null;
        Main.panel.set_style(null);
        Main.panel.remove_style_class_name('macbar-panel');
    }
}
