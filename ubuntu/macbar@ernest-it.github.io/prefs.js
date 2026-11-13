// MacBar Settings: menu bar, dock, weather and the Workspaces editor.
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';
import Soup from 'gi://Soup?version=3.0';
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

const WS_FILE = GLib.build_filenamev([GLib.get_user_config_dir(), 'macbar', 'workspaces.json']);

function loadWorkspaces() {
    try {
        const [ok, data] = GLib.file_get_contents(WS_FILE);
        const list = ok ? JSON.parse(new TextDecoder().decode(data)) : [];
        return Array.isArray(list) ? list : [];
    } catch (e) {
        return [];
    }
}

function saveWorkspaces(list) {
    GLib.mkdir_with_parents(GLib.path_get_dirname(WS_FILE), 0o755);
    GLib.file_set_contents(WS_FILE, JSON.stringify(list, null, 2));
}

function chromeProfiles() {
    const out = [];
    for (const r of ['google-chrome', 'chromium', 'BraveSoftware/Brave-Browser', 'microsoft-edge']) {
        try {
            const [ok, data] = GLib.file_get_contents(GLib.build_filenamev([GLib.get_user_config_dir(), r, 'Local State']));
            if (!ok) continue;
            const cache = JSON.parse(new TextDecoder().decode(data)).profile?.info_cache || {};
            for (const [dir, info] of Object.entries(cache)) out.push({dir, name: info.name || dir, user: info.user_name || ''});
        } catch (e) { /* not installed */ }
    }
    return out;
}

function isChrome(appId) {
    return /google-chrome|chromium|com\.google\.chrome|brave|microsoft-edge/i.test(appId || '');
}

function switchRow(settings, key, title, subtitle = '') {
    const row = new Adw.SwitchRow({title, subtitle});
    settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
    return row;
}

function comboRow(settings, key, title, values, labels) {
    const row = new Adw.ComboRow({title, model: Gtk.StringList.new(labels)});
    const sync = () => { row.selected = Math.max(0, values.indexOf(settings.get_string(key))); };
    sync();
    row.connect('notify::selected', () => settings.set_string(key, values[row.selected]));
    settings.connect(`changed::${key}`, sync);
    return row;
}

function spinRow(settings, key, title, lower, upper, step, digits, isInt) {
    const adj = new Gtk.Adjustment({lower, upper, step_increment: step});
    const row = new Adw.SpinRow({title, adjustment: adj, digits});
    row.value = isInt ? settings.get_int(key) : settings.get_double(key);
    row.connect('notify::value', () => {
        if (isInt) settings.set_int(key, Math.round(row.value)); else settings.set_double(key, row.value);
    });
    return row;
}

export default class MacBarPrefs extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const s = this.getSettings();
        window.set_default_size(720, 760);

        // ----- General -----
        const general = new Adw.PreferencesPage({title: 'Menu bar', icon_name: 'preferences-desktop-display-symbolic'});
        const look = new Adw.PreferencesGroup({title: 'Look', description: 'Applies to the menu bar and the dock.'});
        look.add(spinRow(s, 'bar-opacity', 'Background opacity', 0, 1, 0.05, 2, false));
        look.add(comboRow(s, 'blur', 'Dock blur', ['none', 'light', 'strong'], ['None', 'Light', 'Strong']));
        general.add(look);
        const items = new Adw.PreferencesGroup({title: 'Items'});
        items.add(switchRow(s, 'show-app-name', 'Active app name'));
        items.add(switchRow(s, 'show-now-playing', 'Now Playing island', 'Album art and controls for the music that is playing'));
        items.add(switchRow(s, 'show-weather', 'Weather'));
        items.add(switchRow(s, 'show-sound', 'Sound menu', 'Volume, output device, microphone and microphone switch'));
        items.add(switchRow(s, 'show-lighting', 'OpenRGB lighting profiles', 'Shown while OpenRGB runs with its SDK server on'));
        items.add(spinRow(s, 'openrgb-port', 'OpenRGB SDK port', 1, 65535, 1, 0, true));
        items.add(switchRow(s, 'clock-on-right', 'Clock on the right'));
        general.add(items);
        const behave = new Adw.PreferencesGroup({title: 'Behavior'});
        behave.add(switchRow(s, 'super-opens-apps', 'Super key opens the app grid'));
        behave.add(switchRow(s, 'peek-fullscreen', 'Show the menu bar over full-screen apps',
            'Hold the mouse at the top edge in a full-screen game or video'));
        general.add(behave);
        window.add(general);

        // ----- Dock -----
        const dock = new Adw.PreferencesPage({title: 'Dock', icon_name: 'view-app-grid-symbolic'});
        const dg = new Adw.PreferencesGroup({title: 'Dock',
            description: 'Right-click an app in the dock to keep it or remove it. Drag icons to reorder them, or drag apps in from the app grid.'});
        dg.add(switchRow(s, 'show-dock', 'Show the dock'));
        dg.add(spinRow(s, 'dock-icon-size', 'Icon size', 24, 96, 2, 0, true));
        dg.add(spinRow(s, 'dock-magnify', 'Hover magnification', 1, 2, 0.05, 2, false));
        dock.add(dg);
        dock.add(this._hidingGroup(s));
        window.add(dock);

        // ----- Weather -----
        window.add(this._weatherPage(s));

        // ----- Workspaces -----
        window.add(this._workspacesPage());
    }

    _hidingGroup(s) {
        const group = new Adw.PreferencesGroup({title: 'Hiding'});
        // Off: the dock always stays on screen and maximized windows stop above it.
        const auto = new Adw.ExpanderRow({title: 'Hide the dock automatically', show_enable_switch: true,
            subtitle: 'Off: the dock always stays on screen'});
        const styles = ['always', 'intellihide'];
        const when = new Adw.ComboRow({title: 'When to hide',
            model: Gtk.StringList.new(['Always (like macOS): show it at the bottom edge', 'When a window overlaps it'])});
        auto.add_row(when);
        group.add(auto);
        let syncing = false;
        const sync = () => {
            syncing = true;
            const mode = s.get_string('dock-autohide');
            auto.enable_expansion = mode !== 'never';
            auto.expanded = mode !== 'never';
            auto.subtitle = mode === 'never' ? 'Off: the dock always stays on screen'
                : 'The dock slides away and comes back when you need it';
            if (mode !== 'never') when.selected = Math.max(0, styles.indexOf(mode));
            syncing = false;
        };
        sync();
        auto.connect('notify::enable-expansion', () => {
            if (syncing) return;
            s.set_string('dock-autohide', auto.enable_expansion ? styles[when.selected] : 'never');
        });
        when.connect('notify::selected', () => {
            if (!syncing && auto.enable_expansion) s.set_string('dock-autohide', styles[when.selected]);
        });
        const id = s.connect('changed::dock-autohide', sync);
        group.connect('destroy', () => s.disconnect(id));
        return group;
    }

    _weatherPage(s) {
        const page = new Adw.PreferencesPage({title: 'Weather', icon_name: 'weather-few-clouds-symbolic'});
        const g = new Adw.PreferencesGroup({title: 'Location',
            description: 'Weather comes from Open-Meteo.com: no account and no location access. Search for your city.'});
        const current = new Adw.ActionRow({title: 'Current city', subtitle: s.get_string('weather-city') || 'Not set'});
        s.connect('changed::weather-city', () => { current.subtitle = s.get_string('weather-city'); });
        g.add(current);
        const search = new Adw.EntryRow({title: 'Search for a city'});
        g.add(search);
        const results = new Adw.PreferencesGroup();
        const session = new Soup.Session({timeout: 10, user_agent: 'MacBar-Ubuntu'});
        let rows = [];
        const runSearch = () => {
            const q = search.text.trim();
            if (!q) return;
            const msg = Soup.Message.new('GET',
                `https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name=${encodeURIComponent(q)}`);
            session.send_and_read_async(msg, GLib.PRIORITY_DEFAULT, null, (ss, res) => {
                rows.forEach(r => results.remove(r));
                rows = [];
                let list = [];
                try {
                    list = JSON.parse(new TextDecoder().decode(ss.send_and_read_finish(res).get_data())).results || [];
                } catch (e) {
                    const r = new Adw.ActionRow({title: `Search failed: ${e.message}`});
                    results.add(r); rows.push(r);
                    return;
                }
                if (!list.length) {
                    const r = new Adw.ActionRow({title: 'No matches'});
                    results.add(r); rows.push(r);
                }
                for (const c of list) {
                    const sub = [c.admin1, c.country].filter(x => x).join(', ');
                    const r = new Adw.ActionRow({title: c.name, subtitle: sub, activatable: true});
                    r.add_suffix(new Gtk.Image({icon_name: 'go-next-symbolic'}));
                    r.connect('activated', () => {
                        s.set_double('weather-lat', c.latitude);
                        s.set_double('weather-lon', c.longitude);
                        s.set_string('weather-city', c.name);
                        rows.forEach(x => results.remove(x));
                        rows = [];
                        search.text = '';
                    });
                    results.add(r); rows.push(r);
                }
            });
        };
        search.connect('apply', runSearch);
        search.show_apply_button = true;
        search.connect('entry-activated', runSearch);
        page.add(g);
        page.add(results);
        const units = new Adw.PreferencesGroup({title: 'Units'});
        units.add(switchRow(s, 'weather-fahrenheit', 'Fahrenheit and mph', 'Off: Celsius and km/h'));
        page.add(units);
        return page;
    }

    _workspacesPage() {
        const page = new Adw.PreferencesPage({title: 'Workspaces', icon_name: 'view-dual-symbolic'});
        const intro = new Adw.PreferencesGroup({title: 'Workspaces',
            description: 'To create one: put your windows where you want them, then use the system menu → Workspaces → ' +
                '"Save Open Windows as New Workspace". Run a workspace from the same menu.'});
        page.add(intro);
        let groups = [];
        const profiles = chromeProfiles();

        const rebuild = () => {
            groups.forEach(gr => page.remove(gr));
            groups = [];
            const list = loadWorkspaces();
            if (!list.length) {
                const empty = new Adw.PreferencesGroup({description: 'No workspaces saved yet.'});
                page.add(empty); groups.push(empty);
            }
            const save = () => saveWorkspaces(list);
            list.forEach((ws, wi) => {
                const g = new Adw.PreferencesGroup({title: ws.name});
                const del = new Gtk.Button({icon_name: 'user-trash-symbolic', valign: Gtk.Align.CENTER, tooltip_text: 'Delete this workspace'});
                del.add_css_class('flat');
                del.connect('clicked', () => { list.splice(wi, 1); save(); rebuild(); });
                g.set_header_suffix(del);

                const name = new Adw.EntryRow({title: 'Name', text: ws.name});
                name.connect('changed', () => { ws.name = name.text; g.title = name.text; save(); });
                g.add(name);

                const login = new Adw.SwitchRow({title: 'Run when I sign in', subtitle: 'Only one workspace can run at sign-in', active: !!ws.runAtLogin});
                login.connect('notify::active', () => {
                    if (login.active) list.forEach(o => { o.runAtLogin = false; });
                    ws.runAtLogin = login.active;
                    save();
                    if (login.active) rebuild();
                });
                g.add(login);
                const mode = new Adw.ComboRow({title: 'At sign-in',
                    model: Gtk.StringList.new(['Open missing apps and arrange', 'Only arrange windows as they open']),
                    selected: ws.loginOpensApps === false ? 1 : 0, sensitive: !!ws.runAtLogin});
                mode.connect('notify::selected', () => { ws.loginOpensApps = mode.selected === 0; save(); });
                login.connect('notify::active', () => { mode.sensitive = login.active; });
                g.add(mode);

                ws.apps.forEach((a, ai) => {
                    const where = a.state === 'maximized' ? 'maximized' : a.state === 'minimized' ? 'minimized'
                        : `${a.rect.width}×${a.rect.height} at ${a.rect.x},${a.rect.y}`;
                    const mon = a.monitor ? `monitor ${a.monitor.index + 1} (${a.monitor.width}×${a.monitor.height})` : '';
                    const row = new Adw.ExpanderRow({title: a.name || a.appId || a.wmClass, subtitle: `${mon} · ${where}`});
                    const rm = new Gtk.Button({icon_name: 'list-remove-symbolic', valign: Gtk.Align.CENTER, tooltip_text: 'Remove from this workspace'});
                    rm.add_css_class('flat');
                    rm.connect('clicked', () => { ws.apps.splice(ai, 1); save(); rebuild(); });
                    row.add_suffix(rm);

                    const launch = new Adw.SwitchRow({title: 'Open it if it isn’t running', active: a.launch !== false});
                    launch.connect('notify::active', () => { a.launch = launch.active; save(); });
                    row.add_row(launch);

                    const state = new Adw.ComboRow({title: 'Window state', model: Gtk.StringList.new(['Normal', 'Maximized', 'Minimized']),
                        selected: ['normal', 'maximized', 'minimized'].indexOf(a.state || 'normal')});
                    state.connect('notify::selected', () => { a.state = ['normal', 'maximized', 'minimized'][state.selected]; save(); });
                    row.add_row(state);

                    if (isChrome(a.appId) && profiles.length) {
                        const labels = ['Any profile', ...profiles.map(p => p.user ? `${p.name} (${p.user})` : p.name)];
                        const sel = Math.max(0, profiles.findIndex(p => p.dir === a.chromeProfile) + 1);
                        const prof = new Adw.ComboRow({title: 'Chrome profile', model: Gtk.StringList.new(labels), selected: sel,
                            subtitle: 'Opens this window in this profile'});
                        prof.connect('notify::selected', () => {
                            const p = prof.selected > 0 ? profiles[prof.selected - 1] : null;
                            a.chromeProfile = p ? p.dir : '';
                            a.name = p ? `Google Chrome: ${p.name}` : 'Google Chrome';
                            row.title = a.name;
                            save();
                        });
                        row.add_row(prof);
                    }

                    const hint = new Adw.EntryRow({title: 'Match windows whose title contains (optional)', text: a.titleHint || ''});
                    hint.connect('changed', () => { a.titleHint = hint.text; save(); });
                    row.add_row(hint);
                    g.add(row);
                });
                page.add(g);
                groups.push(g);
            });
        };
        // Picks up workspaces saved from the menu bar while this window is open.
        const reload = new Gtk.Button({icon_name: 'view-refresh-symbolic', valign: Gtk.Align.CENTER, tooltip_text: 'Reload'});
        reload.add_css_class('flat');
        reload.connect('clicked', rebuild);
        intro.set_header_suffix(reload);
        rebuild();
        return page;
    }
}
