// Lighting: lists OpenRGB profiles and loads one, through OpenRGB's SDK server (127.0.0.1:6742).
// Shown only while OpenRGB is running with its SDK server on.
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import St from 'gi://St';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

import {spawn, haveProgram, timeout, removeSource} from './util.js';

Gio._promisify(Gio.SocketClient.prototype, 'connect_to_host_async');
Gio._promisify(Gio.OutputStream.prototype, 'write_all_async');
Gio._promisify(Gio.InputStream.prototype, 'read_bytes_async');

const SET_CLIENT_NAME = 50, REQUEST_PROTOCOL_VERSION = 40, REQUEST_PROFILE_LIST = 150, REQUEST_LOAD_PROFILE = 152;

function packet(id, payload = new Uint8Array(0)) {
    const buf = new Uint8Array(16 + payload.length);
    buf.set([0x4F, 0x52, 0x47, 0x42]); // "ORGB"
    const dv = new DataView(buf.buffer);
    dv.setUint32(4, 0, true);
    dv.setUint32(8, id, true);
    dv.setUint32(12, payload.length, true);
    buf.set(payload, 16);
    return buf;
}

function cString(s) {
    const b = new TextEncoder().encode(s);
    const out = new Uint8Array(b.length + 1);
    out.set(b);
    return out;
}

async function readExactly(stream, n, cancellable) {
    const out = new Uint8Array(n);
    let got = 0;
    while (got < n) {
        const bytes = await stream.read_bytes_async(n - got, GLib.PRIORITY_DEFAULT, cancellable);
        const chunk = bytes.get_data();
        if (!chunk || chunk.length === 0) throw new Error('OpenRGB closed the connection');
        out.set(chunk, got);
        got += chunk.length;
    }
    return out;
}

/** Runs one short session with the SDK server: fn(send, receive). */
async function withOpenRgb(port, fn) {
    const cancellable = new Gio.Cancellable();
    const guard = timeout(2500, () => { cancellable.cancel(); return false; });
    let conn = null;
    try {
        const client = new Gio.SocketClient({timeout: 2});
        conn = await client.connect_to_host_async(`127.0.0.1:${port}`, port, cancellable);
        const out = conn.get_output_stream(), inp = conn.get_input_stream();
        const send = (id, payload) => out.write_all_async(packet(id, payload), GLib.PRIORITY_DEFAULT, cancellable);
        const receive = async wanted => {
            for (;;) {
                const head = await readExactly(inp, 16, cancellable);
                const dv = new DataView(head.buffer);
                const id = dv.getUint32(8, true), size = dv.getUint32(12, true);
                const body = size ? await readExactly(inp, size, cancellable) : new Uint8Array(0);
                if (id === wanted) return body;
            }
        };
        await send(SET_CLIENT_NAME, cString('MacBar'));
        const ver = new Uint8Array(4); new DataView(ver.buffer).setUint32(0, 4, true);
        await send(REQUEST_PROTOCOL_VERSION, ver);
        await receive(REQUEST_PROTOCOL_VERSION);
        return await fn(send, receive);
    } finally {
        removeSource(guard);
        if (conn) { try { conn.close(null); } catch (e) { /* closing anyway */ } }
    }
}

export async function listProfiles(port) {
    return withOpenRgb(port, async (send, receive) => {
        await send(REQUEST_PROFILE_LIST);
        const b = await receive(REQUEST_PROFILE_LIST);
        const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
        let off = 4; // data size
        const count = dv.getUint16(off, true); off += 2;
        const names = [];
        for (let i = 0; i < count && off + 2 <= b.length; i++) {
            const len = dv.getUint16(off, true); off += 2;
            let s = new TextDecoder().decode(b.subarray(off, off + len));
            off += len;
            names.push(s.replace(/\0+$/, ''));
        }
        return names;
    });
}

export function loadProfile(port, name) {
    return withOpenRgb(port, send => send(REQUEST_LOAD_PROFILE, cString(name)));
}

// Profile names that are colors get a matching dot; "off" gets an empty ring; others a rainbow.
const EXTRA_COLORS = {
    mint: '#3EB489', lavender: '#B57EDC', lilac: '#C8A2C8', rose: '#FF66A3', sky: '#87CEEB',
    ice: '#BFEFFF', warm: '#FFB46B', cool: '#8FB8FF', red: '#FF3B30', orange: '#FF9500', yellow: '#FFCC00',
    green: '#34C759', blue: '#007AFF', purple: '#AF52DE', pink: '#FF2D55', white: '#FFFFFF', cyan: '#32ADE6',
    teal: '#30B0C7', magenta: '#FF00FF', gold: '#FFD700', amber: '#FFBF00', violet: '#8F00FF',
};

function swatchStyle(name) {
    const key = name.trim().toLowerCase();
    if (['off', 'none', 'dark', 'black'].includes(key)) return 'border: 2px solid rgba(255,255,255,0.6); background-color: transparent;';
    for (const word of key.split(/[\s_-]+/)) {
        if (EXTRA_COLORS[word]) return `background-color: ${EXTRA_COLORS[word]};`;
    }
    return 'background-gradient-direction: horizontal; background-gradient-start: #FF3B30; background-gradient-end: #007AFF;';
}

export const LightingIndicator = GObject.registerClass(
class LightingIndicator extends PanelMenu.Button {
    _init(settings) {
        super._init(0.5, 'MacBar Lighting', false);
        this._settings = settings;
        this._profiles = [];
        this.add_child(new St.Icon({style_class: 'system-status-icon', icon_name: 'preferences-color-symbolic'}));
        this.visible = false;
        this.menu.connect('open-state-changed', (m, open) => { if (open) this.refresh(); });
        this.refresh();
        this._timer = timeout(15000, () => { this.refresh(); return true; });
    }

    get _port() { return this._settings.get_int('openrgb-port'); }

    async refresh() {
        try {
            this._profiles = await listProfiles(this._port);
            this._error = null;
        } catch (e) {
            this._profiles = null;
            this._error = e.message;
        }
        if (this._destroyed) return;
        this.visible = this._profiles !== null;
        this._build();
    }

    _build(status = null) {
        this.menu.removeAll();
        this.menu.addMenuItem(new PopupMenu.PopupMenuItem('Lighting', {reactive: false, style_class: 'macbar-menu-title'}));
        const current = this._settings.get_string('openrgb-profile');
        if (!this._profiles || this._profiles.length === 0) {
            this.menu.addMenuItem(new PopupMenu.PopupMenuItem(
                this._profiles ? 'No profiles saved in OpenRGB' : 'OpenRGB SDK server not running', {reactive: false}));
        }
        for (const name of this._profiles || []) {
            const it = new PopupMenu.PopupBaseMenuItem();
            it.add_child(new St.Widget({style_class: 'macbar-swatch', style: swatchStyle(name), y_align: Clutter.ActorAlign.CENTER}));
            it.add_child(new St.Label({text: name, x_expand: true, y_align: Clutter.ActorAlign.CENTER}));
            if (name === current) it.add_child(new St.Icon({icon_name: 'object-select-symbolic', icon_size: 14, y_align: Clutter.ActorAlign.CENTER}));
            it.connect('activate', async () => {
                try {
                    await loadProfile(this._port, name);
                    this._settings.set_string('openrgb-profile', name);
                } catch (e) {
                    this._build(`Couldn't load ${name}: ${e.message}`);
                }
            });
            this.menu.addMenuItem(it);
        }
        if (status) this.menu.addMenuItem(new PopupMenu.PopupMenuItem(status, {reactive: false}));
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const open = new PopupMenu.PopupMenuItem('Open OpenRGB…');
        open.connect('activate', () => {
            if (haveProgram('openrgb')) spawn(['openrgb']);
            else spawn(['flatpak', 'run', 'org.openrgb.OpenRGB']);
        });
        this.menu.addMenuItem(open);
    }

    destroy() {
        this._destroyed = true;
        this._timer = removeSource(this._timer);
        super.destroy();
    }
});
