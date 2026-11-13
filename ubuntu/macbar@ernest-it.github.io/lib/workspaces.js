// Workspaces: save where every window goes across monitors and put it back in one click.
// Reuses windows that are already open (only opens what's missing, or opens nothing in "arrange only"
// mode), opens Chrome profiles in the right profile, keeps re-checking for a few seconds so apps that
// restore their own position still end up in place, and can run a workspace at sign-in.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

import {configDir, readText, writeText, uptime, timeout, removeSource} from './util.js';

export const FILE = GLib.build_filenamev([configDir(), 'workspaces.json']);

// Runs at most once per sign-in, even though the extension is disabled/enabled around the lock screen.
let ranAtLoginThisSession = false;

export function load() {
    try {
        const t = readText(FILE);
        const data = t ? JSON.parse(t) : [];
        return Array.isArray(data) ? data : [];
    } catch (e) {
        console.warn(`MacBar: could not read ${FILE}: ${e.message}`);
        return [];
    }
}

export function save(list) {
    writeText(FILE, JSON.stringify(list, null, 2));
}

export function isChrome(appId) {
    return /google-chrome|chromium|com\.google\.chrome|brave|microsoft-edge/i.test(appId || '');
}

/** Chrome profiles from "Local State" (directory -> display name). */
export function chromeProfiles() {
    const roots = ['google-chrome', 'chromium', 'BraveSoftware/Brave-Browser', 'microsoft-edge'];
    const out = [];
    for (const r of roots) {
        const t = readText(GLib.build_filenamev([GLib.get_user_config_dir(), r, 'Local State']));
        if (!t) continue;
        try {
            const cache = JSON.parse(t).profile?.info_cache || {};
            for (const [dir, info] of Object.entries(cache))
                out.push({browser: r, dir, name: info.name || dir, user: info.user_name || ''});
        } catch (e) { /* skip unreadable */ }
    }
    return out;
}

function isMaximized(w) {
    if (typeof w.is_maximized === 'function') return w.is_maximized();
    return w.get_maximized() === Meta.MaximizeFlags.BOTH;
}

function maximize(w) {
    try { w.maximize(Meta.MaximizeFlags.BOTH); } catch (e) { w.maximize(); }
}

function unmaximize(w) {
    try { w.unmaximize(Meta.MaximizeFlags.BOTH); } catch (e) { w.unmaximize(); }
}

function userWindows() {
    return global.get_window_actors()
        .map(a => a.meta_window)
        .filter(w => w && w.get_window_type() === Meta.WindowType.NORMAL && !w.skip_taskbar);
}

function appFor(w) {
    return Shell.WindowTracker.get_default().get_window_app(w);
}

function cmdline(pid) {
    const t = readText(`/proc/${pid}/cmdline`);
    return t ? t.split('\0').filter(s => s) : [];
}

function monitors() {
    const n = global.display.get_n_monitors();
    const out = [];
    for (let i = 0; i < n; i++) {
        const g = global.display.get_monitor_geometry(i);
        out.push({index: i, x: g.x, y: g.y, width: g.width, height: g.height});
    }
    return out;
}

/** The monitor a saved app belongs on now: same geometry, else same index, else the primary one. */
function targetMonitor(slot) {
    const mons = monitors();
    const sm = slot.monitor || {};
    return mons.find(m => m.x === sm.x && m.y === sm.y && m.width === sm.width && m.height === sm.height) ||
        mons.find(m => m.width === sm.width && m.height === sm.height) ||
        mons[sm.index] || mons[global.display.get_primary_monitor()] || mons[0];
}

/** Target frame rect in global coordinates, scaled if the monitor's size changed, kept inside its work area. */
function targetRect(slot, mon) {
    const sm = slot.monitor || mon;
    const sx = sm.width ? mon.width / sm.width : 1, sy = sm.height ? mon.height / sm.height : 1;
    let x = mon.x + Math.round(slot.rect.x * sx), y = mon.y + Math.round(slot.rect.y * sy);
    let w = Math.round(slot.rect.width * sx), h = Math.round(slot.rect.height * sy);
    const wa = Main.layoutManager.getWorkAreaForMonitor(mon.index);
    if (wa) {
        w = Math.min(w, wa.width); h = Math.min(h, wa.height);
        x = Math.max(wa.x, Math.min(x, wa.x + wa.width - w));
        y = Math.max(wa.y, Math.min(y, wa.y + wa.height - h));
    }
    return {x, y, width: w, height: h};
}

function describe(w) {
    const app = appFor(w);
    const appId = app && !app.is_window_backed() ? app.get_id() : '';
    const wmClass = w.get_wm_class() || '';
    const mi = w.get_monitor();
    const g = global.display.get_monitor_geometry(mi);
    const r = w.get_frame_rect();
    let name = app ? app.get_name() : (wmClass || w.get_title());
    const slot = {
        name, appId, wmClass,
        titleHint: '',
        chromeProfile: '',
        launch: true,
        launchCmd: '',
        state: w.minimized ? 'minimized' : isMaximized(w) ? 'maximized' : 'normal',
        workspace: w.is_on_all_workspaces() ? null : w.get_workspace().index(),
        monitor: {index: mi, x: g.x, y: g.y, width: g.width, height: g.height},
        rect: {x: r.x - g.x, y: r.y - g.y, width: r.width, height: r.height},
    };
    if (!appId) {
        // No .desktop file: remember the command line so it can be started again.
        const pid = w.get_pid();
        if (pid > 0) slot.launchCmd = cmdline(pid).map(a => GLib.shell_quote(a)).join(' ');
    }
    return slot;
}

function slotMatches(slot, w) {
    const app = appFor(w);
    if (slot.appId && app && app.get_id() === slot.appId) return true;
    if (!slot.appId && slot.wmClass && (w.get_wm_class() || '').toLowerCase() === slot.wmClass.toLowerCase()) return true;
    return false;
}

export class Workspaces {
    constructor(ext) {
        this._ext = ext;
        this._gen = 0;
        this._list = load();
        // Reload when MacBar Settings (a separate process) edits the file.
        try {
            this._monitor = Gio.File.new_for_path(FILE).monitor_file(Gio.FileMonitorFlags.NONE, null);
            this._monitorId = this._monitor.connect('changed', () => { this._list = load(); });
        } catch (e) { this._monitor = null; }
    }

    list() { return this._list; }

    _save() { save(this._list); }

    capture() {
        // Bottom of the stacking order first, so restoring raises them in the same order.
        const wins = global.display.sort_windows_by_stacking(userWindows());
        return wins.map(describe);
    }

    captureNew() {
        const apps = this.capture();
        let n = this._list.length + 1;
        while (this._list.some(w => w.name === `Workspace ${n}`)) n++;
        const ws = {id: GLib.uuid_string_random(), name: `Workspace ${n}`, runAtLogin: false, loginOpensApps: true, apps};
        this._list.push(ws);
        this._save();
        Main.notify('MacBar Workspaces', `Saved "${ws.name}" with ${apps.length} window${apps.length === 1 ? '' : 's'}. Rename it or pick Chrome profiles in MacBar Settings.`);
        return ws;
    }

    /** Re-reads positions from the open windows; keeps names, Chrome profiles and launch choices. */
    updateFromOpen(ws) {
        const fresh = this.capture();
        const old = ws.apps.slice();
        ws.apps = fresh.map(s => {
            const i = old.findIndex(o => (o.appId && o.appId === s.appId) || (!o.appId && o.wmClass === s.wmClass));
            if (i < 0) return s;
            const o = old.splice(i, 1)[0];
            return {...s, name: o.name || s.name, launch: o.launch, chromeProfile: o.chromeProfile, titleHint: o.titleHint, launchCmd: o.launchCmd || s.launchCmd};
        });
        this._save();
    }

    runAtLogin() {
        if (ranAtLoginThisSession) return;
        ranAtLoginThisSession = true;
        if (uptime() > 600) return; // enabled later in the session, not a fresh sign-in
        const ws = this._list.find(w => w.runAtLogin);
        if (!ws) return;
        this._loginTimer = timeout(4000, () => {
            this._loginTimer = 0;
            this.run(ws, ws.loginOpensApps !== false, 90);
            return false;
        });
    }

    /**
     * Arranges a workspace. openMissing: start apps that have no window (only those marked to launch).
     * watchSeconds: keep matching new windows and re-placing ones that move for this long.
     */
    run(ws, openMissing, watchSeconds = 0) {
        const gen = ++this._gen;
        this._cancelRun();
        const slots = ws.apps.map(a => ({a, w: null, placedAt: 0, checks: [], launchedAt: 0}));
        const startedAt = Date.now();
        const before = new Set(userWindows());
        const result = {placed: 0, opened: 0, failed: []};
        const launchedKeys = new Set();

        const assign = () => {
            const taken = new Set(slots.filter(s => s.w).map(s => s.w));
            const free = userWindows().filter(w => !taken.has(w) && w.get_compositor_private());
            // Slots with a title hint or a launch pending pick first.
            const order = slots.filter(s => !s.w).sort((p, q) => (q.a.titleHint ? 1 : 0) - (p.a.titleHint ? 1 : 0));
            for (const s of order) {
                const mon = targetMonitor(s.a);
                const t = targetRect(s.a, mon);
                let best = null, bestScore = -Infinity;
                for (const w of free) {
                    if (!slotMatches(s.a, w)) continue;
                    let score = 0;
                    const title = w.get_title() || '';
                    if (s.a.titleHint && title.toLowerCase().includes(s.a.titleHint.toLowerCase())) score += 1e6;
                    // A window that appeared after we launched for this slot is very likely its window.
                    if (s.launchedAt && !before.has(w)) score += 5e5;
                    const r = w.get_frame_rect();
                    score -= Math.hypot(r.x + r.width / 2 - (t.x + t.width / 2), r.y + r.height / 2 - (t.y + t.height / 2));
                    if (score > bestScore) { bestScore = score; best = w; }
                }
                if (best) {
                    s.w = best;
                    free.splice(free.indexOf(best), 1);
                    s.unmanagedId = best.connect('unmanaged', () => { s.w = null; s.unmanagedId = 0; });
                    this._place(s);
                }
            }
        };

        const launchMissing = () => {
            for (const s of slots) {
                if (s.w || !s.a.launch) continue;
                const key = `${s.a.appId || s.a.wmClass}|${s.a.chromeProfile || ''}`;
                const app = s.a.appId ? Shell.AppSystem.get_default().lookup_app(s.a.appId) : null;
                const running = app && app.state !== Shell.AppState.STOPPED;
                if (launchedKeys.has(key) && !(running && app.can_open_new_window())) continue;
                s.launchedAt = Date.now();
                if (this._launch(s.a, app, launchedKeys.has(key))) result.opened++;
                else result.failed.push(`${s.a.name} (couldn't start)`);
                launchedKeys.add(key);
            }
        };

        assign();
        if (openMissing) launchMissing();
        const anyLaunched = slots.some(s => s.launchedAt);
        const deadline = startedAt + 1000 * Math.max(watchSeconds, anyLaunched ? 45 : 7);
        let reported = false;
        const report = force => {
            if (reported) return;
            const done = slots.every(s => s.w || (!openMissing || !s.a.launch));
            if (!done && !force) return;
            reported = true;
            const placed = slots.filter(s => s.w).length;
            const missing = slots.filter(s => !s.w).map(s => s.a.name);
            let body = `Arranged ${placed} window${placed === 1 ? '' : 's'}`;
            if (result.opened) body += `, opened ${result.opened} app${result.opened === 1 ? '' : 's'}`;
            body += '.';
            if (missing.length) body += ` Not found: ${missing.join(', ')}.`;
            if (result.failed.length) body += ` ${result.failed.join(', ')}.`;
            Main.notify(`Workspace "${ws.name}"`, body);
        };

        this._runTimer = timeout(250, () => {
            if (gen !== this._gen) return false;
            const now = Date.now();
            if (slots.some(s => !s.w)) assign();
            // Re-check placements: apps often move their window once more after it first shows.
            for (const s of slots) {
                if (!s.w || !s.placedAt) continue;
                const age = now - s.placedAt;
                for (const at of [600, 1500, 3000, 6000]) {
                    if (age >= at && !s.checks.includes(at)) {
                        s.checks.push(at);
                        // A window the user has since made full screen is left alone.
                        if (!s.w.is_fullscreen() && !this._inPlace(s)) this._place(s, true);
                    }
                }
            }
            report(false);
            if (now >= deadline) {
                report(true);
                for (const s of slots) if (s.w && s.unmanagedId) { s.w.disconnect(s.unmanagedId); s.unmanagedId = 0; }
                this._runTimer = 0;
                return false;
            }
            return true;
        });
        return result;
    }

    _cancelRun() {
        this._runTimer = removeSource(this._runTimer);
    }

    _launch(a, app, again) {
        try {
            if (a.chromeProfile && isChrome(a.appId) && app) {
                const exe = app.get_app_info()?.get_executable() || 'google-chrome';
                GLib.spawn_async(null, [exe, `--profile-directory=${a.chromeProfile}`, '--new-window'], null,
                    GLib.SpawnFlags.SEARCH_PATH | GLib.SpawnFlags.DO_NOT_REAP_CHILD, null);
                return true;
            }
            if (app) {
                // A running app gets a new window instead of a second copy of the program.
                if (app.state === Shell.AppState.STOPPED && !again) app.activate();
                else app.open_new_window(-1);
                return true;
            }
            if (a.launchCmd) {
                const [, argv] = GLib.shell_parse_argv(a.launchCmd);
                GLib.spawn_async(null, argv, null, GLib.SpawnFlags.SEARCH_PATH | GLib.SpawnFlags.DO_NOT_REAP_CHILD, null);
                return true;
            }
        } catch (e) {
            console.warn(`MacBar: could not launch ${a.name}: ${e.message}`);
        }
        return false;
    }

    _inPlace(s) {
        const w = s.w, a = s.a;
        if (a.state === 'minimized') return w.minimized;
        if (a.state === 'maximized') return isMaximized(w) && w.get_monitor() === targetMonitor(a).index;
        if (w.minimized || isMaximized(w)) return false;
        const t = targetRect(a, targetMonitor(a));
        const r = w.get_frame_rect();
        // A window bigger than asked is fine when the app has a minimum size.
        const sizeOk = (have, want) => Math.abs(have - want) <= 4 || have > want;
        return Math.abs(r.x - t.x) <= 2 && Math.abs(r.y - t.y) <= 2 && sizeOk(r.width, t.width) && sizeOk(r.height, t.height);
    }

    _place(s, quiet = false) {
        const w = s.w, a = s.a;
        if (!w || !w.get_compositor_private()) return;
        try {
            const mon = targetMonitor(a);
            if (w.is_fullscreen()) w.unmake_fullscreen();
            if (w.minimized && a.state !== 'minimized') w.unminimize();
            if (isMaximized(w) && a.state !== 'maximized') unmaximize(w);
            const nws = global.workspace_manager.get_n_workspaces();
            if (a.workspace !== null && a.workspace !== undefined && !w.is_on_all_workspaces()) {
                if (a.workspace < nws) w.change_workspace_by_index(a.workspace, false);
            }
            if (w.get_monitor() !== mon.index) w.move_to_monitor(mon.index);
            if (a.state === 'maximized') {
                maximize(w);
            } else {
                if (isMaximized(w)) unmaximize(w);
                const t = targetRect(a, mon);
                w.move_resize_frame(true, t.x, t.y, t.width, t.height);
                // If the app can't take that size (minimum size), newer Mutter drops the move too; move separately.
                w.move_frame(true, t.x, t.y);
                if (a.state === 'minimized') w.minimize();
            }
            if (!quiet) s.placedAt = Date.now();
        } catch (e) {
            console.warn(`MacBar: could not place ${a.name}: ${e.message}`);
        }
    }

    destroy() {
        this._gen++;
        this._cancelRun();
        this._loginTimer = removeSource(this._loginTimer);
        if (this._monitor) { this._monitor.disconnect(this._monitorId); this._monitor.cancel(); }
    }
}
