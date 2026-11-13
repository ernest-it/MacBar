// Small shared helpers: version checks, process launching, HTTP, icons that may not exist in every theme.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup?version=3.0';
import * as Config from 'resource:///org/gnome/shell/misc/config.js';

export const SHELL_MAJOR = parseInt(Config.PACKAGE_VERSION.split('.')[0], 10);

/** A themed icon with fallbacks, so a missing name in one icon theme doesn't leave a blank. */
export function icon(...names) {
    return Gio.ThemedIcon.new_from_names(names);
}

export function spawn(argv) {
    try {
        GLib.spawn_async(null, argv, null, GLib.SpawnFlags.SEARCH_PATH | GLib.SpawnFlags.DO_NOT_REAP_CHILD, null);
        return true;
    } catch (e) {
        console.warn(`MacBar: could not run ${argv.join(' ')}: ${e.message}`);
        return false;
    }
}

export function haveProgram(name) {
    return GLib.find_program_in_path(name) !== null;
}

/** Opens a GNOME Settings panel; panel names changed over GNOME versions, so pass candidates newest first. */
export function openSettings(...panels) {
    spawn(['gnome-control-center', ...(panels.length ? [panels[0]] : [])]);
}

let _session = null;
function session() {
    if (!_session) _session = new Soup.Session({timeout: 15, user_agent: 'MacBar-Ubuntu'});
    return _session;
}

export function fetchBytes(url, cancellable = null) {
    return new Promise((resolve, reject) => {
        const msg = Soup.Message.new('GET', url);
        session().send_and_read_async(msg, GLib.PRIORITY_DEFAULT, cancellable, (s, res) => {
            try {
                const bytes = s.send_and_read_finish(res);
                const status = msg.get_status();
                if (status !== 200) throw new Error(`HTTP ${status}`);
                resolve(bytes.get_data());
            } catch (e) {
                reject(e);
            }
        });
    });
}

export async function fetchJson(url, cancellable = null) {
    return JSON.parse(new TextDecoder().decode(await fetchBytes(url, cancellable)));
}

export function disposeSession() {
    if (_session) { _session.abort(); _session = null; }
}

export function readText(path) {
    try {
        const [ok, data] = GLib.file_get_contents(path);
        return ok ? new TextDecoder().decode(data) : null;
    } catch (e) {
        return null;
    }
}

export function writeText(path, text) {
    GLib.mkdir_with_parents(GLib.path_get_dirname(path), 0o755);
    GLib.file_set_contents(path, text);
}

export function configDir() {
    return GLib.build_filenamev([GLib.get_user_config_dir(), 'macbar']);
}

export function cacheDir() {
    return GLib.build_filenamev([GLib.get_user_cache_dir(), 'macbar']);
}

/** Seconds since boot (used to tell a fresh sign-in from an extension re-enable after unlocking). */
export function uptime() {
    const t = readText('/proc/uptime');
    return t ? parseFloat(t.split(' ')[0]) : 1e9;
}

export function timeout(ms, fn) {
    return GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => fn() ? GLib.SOURCE_CONTINUE : GLib.SOURCE_REMOVE);
}

export function removeSource(id) {
    if (id) GLib.source_remove(id);
    return 0;
}
