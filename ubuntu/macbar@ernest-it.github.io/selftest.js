// Development self-test. Runs only when GNOME Shell is started with MACBAR_SELFTEST=<output dir>
// (see test/run.sh). Drives the real UI, takes screenshots and checks results.
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

const sleep = ms => new Promise(r => GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => { r(); return GLib.SOURCE_REMOVE; }));

export class SelfTest {
    constructor(ext) {
        this._ext = ext;
        this._out = GLib.getenv('MACBAR_SELFTEST');
        this._results = [];
        this._run().catch(e => this._check('selftest crashed', false, `${e}\n${e.stack}`)).finally(() => this._finish());
    }

    _check(name, ok, detail = '') {
        this._results.push({name, ok: !!ok, detail: String(detail)});
        console.log(`MACBAR-TEST ${ok ? 'PASS' : 'FAIL'} ${name} ${detail}`);
    }

    _shot(name) {
        return new Promise(resolve => {
            try {
                const file = Gio.File.new_for_path(`${this._out}/${name}.png`);
                const stream = file.replace(null, false, Gio.FileCreateFlags.NONE, null);
                new Shell.Screenshot().screenshot(false, stream, (s, res) => {
                    try { s.screenshot_finish(res); } catch (e) { console.log(`MACBAR-TEST shot ${name} failed ${e}`); }
                    stream.close(null);
                    resolve();
                });
            } catch (e) {
                console.log(`MACBAR-TEST shot ${name} failed ${e}`);
                resolve();
            }
        });
    }

    _warp(x, y) {
        Clutter.get_default_backend().get_default_seat().warp_pointer(x, y);
    }

    _windowsOf(appId) {
        const app = Shell.AppSystem.get_default().lookup_app(appId);
        return app ? app.get_windows() : [];
    }

    async _waitFor(fn, ms = 10000) {
        const end = Date.now() + ms;
        while (Date.now() < end) { if (fn()) return true; await sleep(200); }
        return false;
    }

    async _menu(role, name) {
        const ind = Main.panel.statusArea[role];
        this._check(`${role} present`, !!ind);
        if (!ind) return;
        if (!ind.visible) { this._check(`${role} visible`, false, 'hidden'); return; }
        ind.menu.open();
        await sleep(900);
        await this._shot(name);
        ind.menu.close();
        await sleep(300);
    }

    async _run() {
        await sleep(5000);
        const mon = Main.layoutManager.primaryMonitor;
        this._warp(mon.width / 2, mon.height / 2);
        await sleep(500);
        await this._shot('01-desktop');
        this._check('panel style at start', true, `${Main.panel.get_style()} bg=${Main.panel.get_theme_node().get_background_color().to_string()} boxbg=${Main.layoutManager.panelBox.get_theme_node().get_background_color().to_string()} effects=${Main.panel.get_effects().length}/${Main.layoutManager.panelBox.get_effects().length}`);
        this._ext.topBar._applyLook();
        await sleep(300);
        await this._shot('01b-reapplied');

        const dock = this._ext.dock;
        this._check('dock has items', dock && dock._items.length > 0, dock ? dock._items.length : 0);
        this._check('activities hidden', !Main.panel.statusArea.activities?.container.visible);

        await this._menu('macbar-system', '02-system-menu');
        await this._menu('macbar-sound', '03-sound-menu');
        // Weather needs the network; give it time.
        await this._waitFor(() => !!Main.panel.statusArea['macbar-weather']?._data, 15000);
        this._check('weather loaded', !!Main.panel.statusArea['macbar-weather']?._data);
        await this._menu('macbar-weather', '04-weather-menu');
        this._check('lighting hidden without OpenRGB', !Main.panel.statusArea['macbar-lighting']?.visible);

        // Launch two apps from the dock's point of view.
        const appSys = Shell.AppSystem.get_default();
        const editorId = 'org.gnome.TextEditor.desktop', calcId = 'org.gnome.Calculator.desktop';
        // GNOME turns animations off on software rendering (like this test machine); turn them on for this check.
        const sts = St.Settings.get();
        const wasOff = !sts.enable_animations;
        if (wasOff) sts.uninhibit_animations();
        // Record each new window part-way through its open animation (it should start small, near the dock).
        const opened = [];
        const mapId = global.window_manager.connect('map', (wm, actor) => {
            GLib.timeout_add(GLib.PRIORITY_DEFAULT, 40, () => {
                const [, dy] = this._ext.dock.box.get_transformed_position();
                opened.push({title: actor.meta_window?.get_title(), sx: actor.scale_x, ty: actor.translation_y,
                    centerY: actor.y + actor.height / 2 + actor.translation_y, dockY: dy});
                return GLib.SOURCE_REMOVE;
            });
        });
        appSys.lookup_app(editorId)?.activate();
        appSys.lookup_app(calcId)?.activate();
        const launched = await this._waitFor(() => this._windowsOf(editorId).length && this._windowsOf(calcId).length, 20000);
        this._check('apps launched', launched);
        await this._waitFor(() => opened.length >= 2, 5000);
        await sleep(600);
        global.window_manager.disconnect(mapId);
        if (wasOff) sts.inhibit_animations();
        const grow = opened.filter(o => o.sx < 0.9 && o.ty > 0);
        this._check('windows open from the dock', opened.length > 0 && grow.length === opened.length, JSON.stringify(opened));
        const settled = global.get_window_actors().filter(a => a.meta_window.get_window_type() === 0).every(a => a.scale_x === 1 && a.translation_y === 0 && a.opacity === 255);
        this._check('open animation settles', settled);
        const ig = this._windowsOf(calcId)[0]?.get_icon_geometry?.();
        this._check('minimize goes to the dock icon', !!ig && ig[0] && ig[1].y > global.screen_height - 150, ig ? JSON.stringify([ig[1].x, ig[1].y, ig[1].width, ig[1].height]) : 'none');
        await sleep(1500);
        this._check('running apps in dock', dock._items.some(i => i.app && i.app.get_id() === calcId), dock._items.map(i => i.app?.get_id()).join(','));

        // Hover magnification at the dock.
        const calcItem = dock._items.find(i => i.app && i.app.get_id() === calcId);
        if (calcItem) {
            const [ix, iy] = calcItem.get_transformed_position();
            this._warp(ix + calcItem.width / 2, iy + calcItem.height / 2);
            await sleep(700);
            await this._shot('05-dock-hover');
            this._check('hover magnifies', calcItem.iconActor.scale_x > 1.05, calcItem.iconActor.scale_x);
        }
        this._warp(mon.width / 2, mon.height / 2);
        await sleep(600);

        // A window asking for attention (an admin prompt that opened behind others) is reached from the dock.
        const attnWin = this._windowsOf(calcId)[0], edWin0 = this._windowsOf(editorId)[0];
        if (attnWin && edWin0) {
            // Mutter ignores the request for a window that isn't covered, so put it behind the editor.
            const er = edWin0.get_frame_rect();
            attnWin.move_frame(true, er.x + 20, er.y + 20);
            edWin0.move_resize_frame(true, er.x, er.y, Math.max(er.width, 700), Math.max(er.height, 700));
            Main.activateWindow(edWin0);
            await sleep(500);
            attnWin.set_demands_attention();
            await sleep(500);
            const item = dock._items.find(i => i.app && i.app.get_id() === calcId);
            this._check('attention app in dock', !!item && attnWin.demands_attention, `attn=${attnWin.demands_attention}`);
            item?._onClick(1);
            await sleep(500);
            this._check('dock click focuses attention window', global.display.focus_window === attnWin,
                `focus=${global.display.focus_window?.get_title()}`);
        }

        // ----- Workspaces -----
        const ws = this._ext.workspaces;
        const calcWin = this._windowsOf(calcId)[0], edWin = this._windowsOf(editorId)[0];
        const workspace = {
            id: 't', name: 'Test', apps: [
                {name: 'Calculator', appId: calcId, wmClass: '', launch: true, state: 'normal', workspace: 0,
                    monitor: {index: 0, x: mon.x, y: mon.y, width: mon.width, height: mon.height},
                    rect: {x: 1300, y: 200, width: 420, height: 560}},
                {name: 'Text Editor', appId: editorId, wmClass: '', launch: true, state: 'normal', workspace: 0,
                    monitor: {index: 0, x: mon.x, y: mon.y, width: mon.width, height: mon.height},
                    rect: {x: 120, y: 160, width: 900, height: 640}},
            ],
        };
        calcWin.move_resize_frame(true, 50, 600, 400, 400);
        edWin.move_resize_frame(true, 700, 500, 700, 450);
        await sleep(800);
        const ed0 = this._windowsOf(editorId).length, calc0 = this._windowsOf(calcId).length;
        ws.run(workspace, false, 0);
        await sleep(2500);
        await this._shot('06-workspace-arranged');
        const rc = calcWin.get_frame_rect(), re = edWin.get_frame_rect();
        const sz = (have, want) => Math.abs(have - want) <= 6 || have > want; // apps may enforce a minimum size
        const near = (r, x, y, w, h) => Math.abs(r.x - x) <= 3 && Math.abs(r.y - y) <= 3 && sz(r.width, w) && sz(r.height, h);
        this._check('arrange-only places calculator', near(rc, mon.x + 1300, mon.y + 200, 420, 560), `${rc.x},${rc.y} ${rc.width}x${rc.height}`);
        this._check('arrange-only places editor', near(re, mon.x + 120, mon.y + 160, 900, 640), `${re.x},${re.y} ${re.width}x${re.height}`);
        this._check('arrange-only opened nothing', this._windowsOf(editorId).length === ed0 && this._windowsOf(calcId).length === calc0);

        // Maximized state.
        workspace.apps[1].state = 'maximized';
        ws.run(workspace, false, 0);
        await sleep(1500);
        const maxed = typeof edWin.is_maximized === 'function' ? edWin.is_maximized() : edWin.get_maximized() === 3;
        this._check('maximized state restored', maxed);
        workspace.apps[1].state = 'normal';
        ws.run(workspace, false, 0);
        await sleep(1500);

        // Open missing: close the calculator, run with openMissing, expect exactly one calculator back in place.
        calcWin.delete(global.get_current_time());
        await this._waitFor(() => this._windowsOf(calcId).length === 0, 5000);
        ws.run(workspace, true, 0);
        const back = await this._waitFor(() => this._windowsOf(calcId).length === 1, 15000);
        await sleep(3500);
        const nc = this._windowsOf(calcId)[0];
        this._check('open-missing relaunched calculator', back);
        if (nc) {
            const r = nc.get_frame_rect();
            this._check('relaunched calculator placed', near(r, mon.x + 1300, mon.y + 200, 420, 560), `${r.x},${r.y} ${r.width}x${r.height}`);
        }
        const edCount = this._windowsOf(editorId).length;
        ws.run(workspace, true, 0);
        await sleep(4000);
        this._check('second run opens no duplicates', this._windowsOf(calcId).length === 1 && this._windowsOf(editorId).length === edCount,
            `calc ${this._windowsOf(calcId).length}, editor ${this._windowsOf(editorId).length}`);

        // Capture produces entries for both apps.
        const cap = ws.capture();
        this._check('capture sees both apps', cap.some(a => a.appId === calcId) && cap.some(a => a.appId === editorId), cap.map(a => a.appId).join(','));

        // ----- Full screen: bars hide; top edge peeks the menu bar -----
        await sleep(4000); // let the last workspace run finish its re-checks
        Main.activateWindow(edWin);
        await sleep(500);
        edWin.make_fullscreen();
        await sleep(1500);
        const box = Main.layoutManager.panelBox;
        const ui = box.get_parent();
        const at = a => ui.get_children().indexOf(a);
        const boxAt = at(box);
        this._check('fullscreen hides menu bar', !box.visible);
        this._check('fullscreen hides dock', !dock.container.visible);
        this._warp(mon.x + 600, mon.y);
        await sleep(800);
        this._check('top edge peeks menu bar', box.visible, `winFs=${edWin.is_fullscreen()} inFs=${mon.inFullscreen} focus=${global.display.focus_window?.get_title()}`);
        // GNOME's dialogs (the admin password prompt) must stay clickable above the peeking bar.
        const modalAt = at(Main.layoutManager.modalDialogGroup);
        this._check('peek stays below dialogs', at(box) < modalAt, `box=${at(box)} modal=${modalAt}`);
        await this._shot('07-fullscreen-peek');
        this._warp(mon.x + 600, mon.y + 600);
        await sleep(1200);
        const tb = this._ext.topBar;
        this._check('menu bar hides after leaving', !box.visible, `inFs=${mon.inFullscreen} peeking=${tb._peeking} timer=${tb._peekTimer} ptr=${global.get_pointer().slice(0,2)} activeMenu=${!!Main.panel.menuManager.activeMenu} winFs=${edWin.is_fullscreen()}`);
        this._check('peek restores stacking', at(box) === boxAt, `before=${boxAt} after=${at(box)}`);
        // Super key / app grid over a full-screen app brings the dock and bar back.
        Main.overview.showApps();
        await sleep(1800);
        this._check('app grid shows dock', dock.container.visible);
        this._check('app grid shows menu bar', box.visible);
        await this._shot('08-app-grid');
        Main.overview.hide();
        await sleep(1200);
        edWin.unmake_fullscreen();
        await sleep(800);

        // Now Playing island, fed by test/fake-player.js.
        const np = Main.panel.statusArea['macbar-nowplaying'];
        const npSt = this._ext.getSettings();
        await this._waitFor(() => np?.visible && np._title.text, 8000);
        this._check('now playing shows the track', np?.visible && np._title.text === 'First Song', np?._title.text);
        if (np?.visible) {
            this._warp(mon.x + 200, mon.y + 600);
            await sleep(400);
            this._check('music controls hidden until hover', !np._controls.visible && !np._artist.visible);
            await this._shot('11-nowplaying');
            npSt.set_boolean('now-playing-expanded', true);
            await sleep(500);
            this._check('music controls always shown with the setting', np._controls.visible && np._artist.visible, np._artist.text);
            await this._shot('11b-nowplaying-expanded');
            const next = np._controls.get_child_at_index(2);
            const [bx, by] = next.get_transformed_position();
            const [bw, bh] = next.get_transformed_size();
            const dev = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
            const t = () => GLib.get_monotonic_time();
            this._warp(bx + bw / 2, by + bh / 2);
            await sleep(300);
            dev.notify_absolute_motion(t(), bx + bw / 2, by + bh / 2);
            await sleep(200);
            dev.notify_button(t(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.PRESSED);
            await sleep(60);
            dev.notify_button(t(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.RELEASED);
            await this._waitFor(() => np._title.text === 'Second Song', 3000);
            this._check('skip button in the bar works', np._title.text === 'Second Song', np._title.text);
            this._check('skip button does not open the player', !np.menu.isOpen);
            np.menu.close();
            this._warp(mon.x + 200, mon.y + 600);
            npSt.set_boolean('now-playing-expanded', false);
            await sleep(400);
            this._check('music controls hide again when the setting is off', !np._controls.visible);
        }

        // Preferences window.
        GLib.spawn_command_line_async(`gnome-extensions prefs ${this._ext.uuid}`);
        await sleep(6000);
        await this._shot('09-prefs');
        const st = this._ext.getSettings();
        // Click the Dock tab (a virtual pointer, since prefs run in their own process), then flip auto-hide.
        const tab = global.get_window_actors().map(a => a.meta_window).find(w => w.get_title()?.includes('MacBar') || w.get_wm_class()?.includes('Extensions'));
        if (tab) {
            const r = tab.get_frame_rect();
            const dev = Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
            const t = () => GLib.get_monotonic_time();
            console.log(`MACBAR-TEST INFO prefs window ${tab.get_title()} ${r.x},${r.y} ${r.width}x${r.height}`);
            await sleep(500);
            this._warp(r.x + r.width / 2 - 65, r.y + 22);
            await sleep(300);
            dev.notify_absolute_motion(t(), r.x + r.width / 2 - 65, r.y + 22);
            await sleep(200);
            dev.notify_button(t(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.PRESSED);
            dev.notify_button(t(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.RELEASED);
            await sleep(800);
            await this._shot('09b-prefs-dock');
            st.set_string('dock-autohide', 'intellihide');
            await sleep(800);
            await this._shot('09c-prefs-dock-autohide');
            st.set_string('dock-autohide', 'never');
        }
        st.set_string('blur', 'none');
        await sleep(800);
        await this._shot('10-no-blur');
        this._check("panel style", true, "");
        st.set_string('blur', 'light');
    }

    _finish() {
        const failed = this._results.filter(r => !r.ok).length;
        GLib.file_set_contents(`${this._out}/results.json`, JSON.stringify({failed, results: this._results}, null, 2));
        GLib.file_set_contents(`${this._out}/done`, 'done');
    }
}
