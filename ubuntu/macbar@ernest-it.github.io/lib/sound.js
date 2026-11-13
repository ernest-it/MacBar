// Sound menu: volume, output device list, microphone list and a microphone on/off switch.
import Clutter from 'gi://Clutter';
import GObject from 'gi://GObject';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as Slider from 'resource:///org/gnome/shell/ui/slider.js';
import * as Volume from 'resource:///org/gnome/shell/ui/status/volume.js';

import {spawn} from './util.js';

function volumeIcon(muted, v) {
    if (muted || v <= 0) return 'audio-volume-muted-symbolic';
    if (v < 0.34) return 'audio-volume-low-symbolic';
    if (v < 0.67) return 'audio-volume-medium-symbolic';
    return 'audio-volume-high-symbolic';
}

/** A device row: round badge (blue when selected) and the device name, like the Windows MacBar panel. */
const DeviceItem = GObject.registerClass(
class DeviceItem extends PopupMenu.PopupBaseMenuItem {
    _init(name, iconName, selected) {
        super._init({style_class: 'macbar-device-item'});
        const badge = new St.Bin({
            style_class: selected ? 'macbar-badge macbar-badge-active' : 'macbar-badge',
            child: new St.Icon({icon_name: iconName, icon_size: 14}),
            y_align: Clutter.ActorAlign.CENTER,
        });
        this.add_child(badge);
        this.add_child(new St.Label({
            text: name, y_align: Clutter.ActorAlign.CENTER,
            style_class: selected ? 'macbar-device-name macbar-selected' : 'macbar-device-name',
        }));
    }
});

export const SoundIndicator = GObject.registerClass(
class SoundIndicator extends PanelMenu.Button {
    _init() {
        super._init(0.5, 'MacBar Sound', false);
        this._icon = new St.Icon({style_class: 'system-status-icon', icon_name: 'audio-volume-high-symbolic'});
        this.add_child(this._icon);
        this._control = Volume.getMixerControl();
        this._ids = [];
        for (const sig of ['default-sink-changed', 'default-source-changed', 'state-changed',
            'output-added', 'output-removed', 'input-added', 'input-removed', 'active-output-update', 'active-input-update'])
            this._ids.push(this._control.connect(sig, () => this._onControlChanged()));

        this.connect('scroll-event', (a, ev) => this._onScroll(ev));

        // Static parts of the menu; device sections are rebuilt on open and when devices change.
        const title = new PopupMenu.PopupMenuItem('Sound', {reactive: false, style_class: 'macbar-menu-title'});
        this.menu.addMenuItem(title);

        const row = new PopupMenu.PopupBaseMenuItem({activate: false, style_class: 'macbar-slider-row'});
        this._muteButton = new St.Button({style_class: 'macbar-icon-button', child: new St.Icon({icon_size: 16})});
        this._muteButton.connect('clicked', () => {
            const s = this._sink();
            if (s) s.change_is_muted(!s.is_muted);
        });
        this._slider = new Slider.Slider(0);
        this._slider.x_expand = true;
        this._sliderId = this._slider.connect('notify::value', () => this._onSlider());
        this._pct = new St.Label({style_class: 'macbar-pct', y_align: Clutter.ActorAlign.CENTER});
        row.add_child(this._muteButton);
        row.add_child(this._slider);
        row.add_child(this._pct);
        this.menu.addMenuItem(row);

        this._outputs = new PopupMenu.PopupMenuSection();
        this._inputs = new PopupMenu.PopupMenuSection();
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this.menu.addMenuItem(this._outputs);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this.menu.addMenuItem(this._inputs);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const settings = new PopupMenu.PopupMenuItem('Sound Settings…');
        settings.connect('activate', () => { Main.overview.hide(); spawn(['gnome-control-center', 'sound']); });
        this.menu.addMenuItem(settings);

        this.menu.connect('open-state-changed', (m, open) => { if (open) this._buildDevices(); });
        this._onControlChanged();
    }

    _sink() { return this._control.get_default_sink(); }
    _source() { return this._control.get_default_source(); }

    _onControlChanged() {
        // Follow the default output's volume/mute.
        const sink = this._sink();
        if (sink !== this._watchedSink) {
            if (this._watchedSink && this._sinkIds) this._sinkIds.forEach(id => this._watchedSink.disconnect(id));
            this._watchedSink = sink;
            this._sinkIds = sink ? [
                sink.connect('notify::volume', () => this._sync()),
                sink.connect('notify::is-muted', () => this._sync()),
            ] : null;
        }
        const src = this._source();
        if (src !== this._watchedSource) {
            if (this._watchedSource && this._sourceId) this._watchedSource.disconnect(this._sourceId);
            this._watchedSource = src;
            this._sourceId = src ? src.connect('notify::is-muted', () => { if (this.menu.isOpen) this._buildDevices(); }) : 0;
        }
        this._sync();
        if (this.menu.isOpen) this._buildDevices();
    }

    _level() {
        const s = this._sink();
        if (!s) return 0;
        return s.volume / this._control.get_vol_max_norm();
    }

    _sync() {
        const s = this._sink();
        this.visible = true;
        const v = this._level();
        const muted = !s || s.is_muted;
        const name = volumeIcon(muted, v);
        this._icon.icon_name = name;
        this._muteButton.child.icon_name = name;
        this._pct.text = muted ? 'Muted' : `${Math.round(v * 100)}%`;
        this._syncing = true;
        this._slider.value = Math.min(1, v);
        this._syncing = false;
    }

    _onSlider() {
        if (this._syncing) return;
        const s = this._sink();
        if (!s) return;
        const v = this._slider.value;
        s.volume = v * this._control.get_vol_max_norm();
        s.push_volume();
        if (s.is_muted && v > 0) s.change_is_muted(false);
    }

    _onScroll(ev) {
        const s = this._sink();
        if (!s) return Clutter.EVENT_PROPAGATE;
        const dir = ev.get_scroll_direction();
        let delta = 0;
        if (dir === Clutter.ScrollDirection.UP) delta = 0.05;
        else if (dir === Clutter.ScrollDirection.DOWN) delta = -0.05;
        else if (dir === Clutter.ScrollDirection.SMOOTH) delta = -ev.get_scroll_delta()[1] * 0.05;
        const v = Math.max(0, Math.min(1, this._level() + delta));
        s.volume = v * this._control.get_vol_max_norm();
        s.push_volume();
        if (s.is_muted && v > 0) s.change_is_muted(false);
        return Clutter.EVENT_STOP;
    }

    /** All UI devices of one direction. Gvc only hands them out by id, so look the ids up. */
    _devices(input) {
        const out = [];
        let misses = 0;
        for (let id = 0; id < 512 && misses < 64; id++) {
            const d = input ? this._control.lookup_input_id(id) : this._control.lookup_output_id(id);
            if (!d) { misses++; continue; }
            misses = 0;
            out.push(d);
        }
        return out;
    }

    _activeId(input) {
        const stream = input ? this._source() : this._sink();
        if (!stream) return -1;
        const d = this._control.lookup_device_from_stream(stream);
        return d ? d.get_id() : -1;
    }

    _label(d) {
        const desc = d.get_description() || 'Unknown device';
        const origin = d.get_origin();
        return origin ? `${desc} – ${origin}` : desc;
    }

    _buildDevices() {
        this._fill(this._outputs, false);
        this._fill(this._inputs, true);
    }

    _fill(section, input) {
        section.removeAll();
        const devices = this._devices(input);
        if (input) {
            const src = this._source();
            if (src) {
                const sw = new PopupMenu.PopupSwitchMenuItem(src.is_muted ? 'Microphone (muted)' : 'Microphone', !src.is_muted);
                sw.add_style_class_name('macbar-mic-switch');
                sw.connect('toggled', (item, on) => src.change_is_muted(!on));
                section.addMenuItem(sw);
            } else {
                section.addMenuItem(new PopupMenu.PopupMenuItem('No microphone', {reactive: false, style_class: 'macbar-menu-heading'}));
            }
        } else {
            section.addMenuItem(new PopupMenu.PopupMenuItem('Output', {reactive: false, style_class: 'macbar-menu-heading'}));
        }
        const active = this._activeId(input);
        const muted = input && this._source() ? this._source().is_muted : false;
        for (const d of devices) {
            const sel = d.get_id() === active;
            const iconName = input ? (sel && muted ? 'microphone-disabled-symbolic' : 'audio-input-microphone-symbolic') : 'audio-speakers-symbolic';
            const it = new DeviceItem(this._label(d), iconName, sel);
            it.connect('activate', () => {
                if (sel) return;
                if (input) this._control.change_input(d); else this._control.change_output(d);
            });
            section.addMenuItem(it);
        }
    }

    destroy() {
        this._ids.forEach(id => this._control.disconnect(id));
        if (this._watchedSink && this._sinkIds) this._sinkIds.forEach(id => this._watchedSink.disconnect(id));
        if (this._watchedSource && this._sourceId) this._watchedSource.disconnect(this._sourceId);
        super.destroy();
    }
});
