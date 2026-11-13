// Now Playing island in the middle of the menu bar: album art, title and a live equalizer while music
// plays; click for a player with art, a seekable progress bar and controls. Works with any MPRIS player
// (Spotify, Firefox, Chrome, Rhythmbox, VLC...).
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import St from 'gi://St';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as Slider from 'resource:///org/gnome/shell/ui/slider.js';

import {fetchBytes, cacheDir, timeout, removeSource} from './util.js';

const PREFIX = 'org.mpris.MediaPlayer2.';
const PATH = '/org/mpris/MediaPlayer2';

class Player {
    constructor(busName, onChange) {
        this.busName = busName;
        this._onChange = onChange;
        this.proxy = null;
        Gio.DBusProxy.new_for_bus(Gio.BusType.SESSION, Gio.DBusProxyFlags.GET_INVALIDATED_PROPERTIES, null,
            busName, PATH, 'org.mpris.MediaPlayer2.Player', null, (src, res) => {
                try {
                    this.proxy = Gio.DBusProxy.new_for_bus_finish(res);
                } catch (e) {
                    return;
                }
                if (this._destroyed) return;
                this._propsId = this.proxy.connect('g-properties-changed', () => this._onChange());
                this._seekedId = this.proxy.connect('g-signal', (p, sender, signal) => {
                    if (signal === 'Seeked') this._onChange();
                });
                this._onChange();
            });
    }

    _prop(name) {
        const v = this.proxy ? this.proxy.get_cached_property(name) : null;
        return v ? v.recursiveUnpack() : null;
    }

    get status() { return this._prop('PlaybackStatus') || 'Stopped'; }
    get metadata() { return this._prop('Metadata') || {}; }
    get title() { return this.metadata['xesam:title'] || ''; }
    get artist() {
        const a = this.metadata['xesam:artist'];
        return Array.isArray(a) ? a.join(', ') : (a || '');
    }
    get artUrl() { return this.metadata['mpris:artUrl'] || ''; }
    get length() { return Number(this.metadata['mpris:length'] || 0); }
    get trackId() { return this.metadata['mpris:trackid'] || '/org/mpris/MediaPlayer2/TrackList/NoTrack'; }
    get canSeek() { return !!this._prop('CanSeek'); }

    call(method, params = null) {
        if (this.proxy) this.proxy.call(method, params, Gio.DBusCallFlags.NONE, -1, null, null);
    }

    /** Position isn't pushed as a change signal, so ask for it. */
    position() {
        return new Promise(resolve => {
            Gio.DBus.session.call(this.busName, PATH, 'org.freedesktop.DBus.Properties', 'Get',
                new GLib.Variant('(ss)', ['org.mpris.MediaPlayer2.Player', 'Position']), null,
                Gio.DBusCallFlags.NONE, 1000, null, (c, res) => {
                    try {
                        resolve(Number(c.call_finish(res).recursiveUnpack()[0]));
                    } catch (e) {
                        resolve(0);
                    }
                });
        });
    }

    seek(us) {
        this.call('SetPosition', new GLib.Variant('(ox)', [this.trackId, Math.round(us)]));
    }

    destroy() {
        this._destroyed = true;
        if (this.proxy) {
            if (this._propsId) this.proxy.disconnect(this._propsId);
            if (this._seekedId) this.proxy.disconnect(this._seekedId);
        }
    }
}

function fmt(us) {
    const s = Math.max(0, Math.floor(us / 1e6));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/** Gets a local file URI for album art (remote art is downloaded to the cache). */
async function localArt(url) {
    if (!url) return null;
    if (url.startsWith('file://')) return url;
    if (!url.startsWith('http')) return null;
    const dir = GLib.build_filenamev([cacheDir(), 'art']);
    GLib.mkdir_with_parents(dir, 0o755);
    const path = GLib.build_filenamev([dir, GLib.compute_checksum_for_string(GLib.ChecksumType.SHA1, url, -1)]);
    if (!GLib.file_test(path, GLib.FileTest.EXISTS)) {
        const data = await fetchBytes(url);
        GLib.file_set_contents(path, data);
    }
    return GLib.filename_to_uri(path, null);
}

export const NowPlayingIndicator = GObject.registerClass(
class NowPlayingIndicator extends PanelMenu.Button {
    _init() {
        super._init(0.5, 'MacBar Now Playing', false);
        this.add_style_class_name('macbar-island');
        const box = new St.BoxLayout({style_class: 'macbar-island-box'});
        this._art = new St.Bin({style_class: 'macbar-island-art', y_align: Clutter.ActorAlign.CENTER});
        this._title = new St.Label({style_class: 'macbar-island-title', y_align: Clutter.ActorAlign.CENTER});
        this._title.clutter_text.ellipsize = 3; // END
        this._eq = new St.BoxLayout({style_class: 'macbar-eq', y_align: Clutter.ActorAlign.CENTER});
        this._bars = [0, 1, 2, 3].map(() => {
            const b = new St.Widget({style_class: 'macbar-eq-bar', y_align: Clutter.ActorAlign.END});
            this._eq.add_child(b);
            return b;
        });
        box.add_child(this._art);
        box.add_child(this._title);
        box.add_child(this._eq);
        this.add_child(box);
        this.visible = false;

        this._players = new Map();
        this._buildMenu();
        this._watchBus();
        this.menu.connect('open-state-changed', (m, open) => {
            if (open) {
                this._updatePosition();
                this._posTimer = timeout(1000, () => { this._updatePosition(); return true; });
            } else {
                this._posTimer = removeSource(this._posTimer);
            }
        });
    }

    _watchBus() {
        this._nameSub = Gio.DBus.session.signal_subscribe('org.freedesktop.DBus', 'org.freedesktop.DBus',
            'NameOwnerChanged', '/org/freedesktop/DBus', null, Gio.DBusSignalFlags.NONE,
            (c, sender, path, iface, signal, params) => {
                const [name, oldOwner, newOwner] = params.deepUnpack();
                if (!name.startsWith(PREFIX)) return;
                if (newOwner && !oldOwner) this._addPlayer(name);
                else if (oldOwner && !newOwner) this._removePlayer(name);
            });
        Gio.DBus.session.call('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'ListNames',
            null, null, Gio.DBusCallFlags.NONE, -1, null, (c, res) => {
                try {
                    const [names] = c.call_finish(res).deepUnpack();
                    names.filter(n => n.startsWith(PREFIX)).forEach(n => this._addPlayer(n));
                } catch (e) { /* no bus names yet */ }
            });
    }

    _addPlayer(name) {
        if (this._players.has(name) || this._destroyed) return;
        this._players.set(name, new Player(name, () => this._update()));
    }

    _removePlayer(name) {
        const p = this._players.get(name);
        if (!p) return;
        p.destroy();
        this._players.delete(name);
        this._update();
    }

    /** The player to show: one that's playing, else one that's paused with a track. */
    _pick() {
        const all = [...this._players.values()].filter(p => p.proxy && p.title);
        return all.find(p => p.status === 'Playing') || all.find(p => p.status === 'Paused') || null;
    }

    async _update() {
        if (this._destroyed) return;
        const p = this._pick();
        this._current = p;
        this.visible = !!p;
        if (!p) { this._eqStop(); return; }
        const playing = p.status === 'Playing';
        this._title.text = p.title;
        if (playing) this._eqStart(); else this._eqStop();
        this._eq.visible = playing;

        this._mTitle.text = p.title;
        this._mArtist.text = p.artist;
        this._playIcon.icon_name = playing ? 'media-playback-pause-symbolic' : 'media-playback-start-symbolic';
        this._slider.reactive = p.canSeek;

        const art = p.artUrl;
        if (art !== this._artUrl) {
            this._artUrl = art;
            let uri = null;
            try { uri = await localArt(art); } catch (e) { uri = null; }
            if (this._destroyed || art !== this._artUrl) return;
            const style = uri ? `background-image: url("${uri}"); background-size: cover;` : null;
            this._art.set_style(style);
            this._mArt.set_style(style);
            this._art.visible = !!uri;
        }
        if (this.menu.isOpen) this._updatePosition();
    }

    async _updatePosition() {
        const p = this._current;
        if (!p || this._dragging) return;
        const len = p.length;
        const pos = await p.position();
        if (this._dragging || !this._current) return;
        this._settingSlider = true;
        this._slider.value = len > 0 ? Math.min(1, pos / len) : 0;
        this._settingSlider = false;
        this._elapsed.text = fmt(pos);
        this._total.text = len > 0 ? fmt(len) : '';
    }

    _eqStart() {
        if (this._eqTimer) return;
        this._eqTimer = timeout(160, () => {
            for (const b of this._bars) b.ease({height: 3 + Math.random() * 10, duration: 150, mode: Clutter.AnimationMode.EASE_OUT_QUAD});
            return true;
        });
    }

    _eqStop() {
        this._eqTimer = removeSource(this._eqTimer);
        for (const b of this._bars) b.height = 3;
    }

    _buildMenu() {
        const item = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false, style_class: 'macbar-player'});
        const col = new St.BoxLayout({vertical: true, x_expand: true, style_class: 'macbar-player-box'});
        this._mArt = new St.Bin({style_class: 'macbar-player-art', x_align: Clutter.ActorAlign.CENTER});
        this._mTitle = new St.Label({style_class: 'macbar-player-title', x_align: Clutter.ActorAlign.CENTER});
        this._mArtist = new St.Label({style_class: 'macbar-player-artist', x_align: Clutter.ActorAlign.CENTER});
        col.add_child(this._mArt);
        col.add_child(this._mTitle);
        col.add_child(this._mArtist);

        this._slider = new Slider.Slider(0);
        this._slider.add_style_class_name('macbar-player-progress');
        this._slider.connect('drag-begin', () => { this._dragging = true; });
        this._slider.connect('drag-end', () => {
            this._dragging = false;
            const p = this._current;
            if (p && p.length > 0) p.seek(this._slider.value * p.length);
        });
        col.add_child(this._slider);
        const times = new St.BoxLayout({style_class: 'macbar-player-times'});
        this._elapsed = new St.Label({text: '0:00', x_expand: true});
        this._total = new St.Label({text: ''});
        times.add_child(this._elapsed);
        times.add_child(this._total);
        col.add_child(times);

        const controls = new St.BoxLayout({style_class: 'macbar-player-controls', x_align: Clutter.ActorAlign.CENTER});
        const btn = (iconName, fn, big) => {
            const ic = new St.Icon({icon_name: iconName, icon_size: big ? 28 : 20});
            const b = new St.Button({style_class: 'macbar-player-button', child: ic, can_focus: true});
            b.connect('clicked', fn);
            controls.add_child(b);
            return ic;
        };
        btn('media-skip-backward-symbolic', () => this._current?.call('Previous'));
        this._playIcon = btn('media-playback-start-symbolic', () => this._current?.call('PlayPause'), true);
        btn('media-skip-forward-symbolic', () => this._current?.call('Next'));
        col.add_child(controls);
        item.add_child(col);
        this.menu.addMenuItem(item);
    }

    destroy() {
        this._destroyed = true;
        this._eqStop();
        this._posTimer = removeSource(this._posTimer);
        if (this._nameSub) Gio.DBus.session.signal_unsubscribe(this._nameSub);
        for (const p of this._players.values()) p.destroy();
        this._players.clear();
        super.destroy();
    }
});
