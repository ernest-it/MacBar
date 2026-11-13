// Weather from Open-Meteo (no API key, no location access): current conditions and a 6-day forecast.
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import St from 'gi://St';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

import {fetchJson, timeout, removeSource} from './util.js';

export function describe(code) {
    const c = Number(code);
    if (c === 0) return 'Clear';
    if (c === 1) return 'Mostly clear';
    if (c === 2) return 'Partly cloudy';
    if (c === 3) return 'Cloudy';
    if (c === 45 || c === 48) return 'Fog';
    if (c >= 51 && c <= 57) return 'Drizzle';
    if (c >= 61 && c <= 67) return 'Rain';
    if (c >= 71 && c <= 77) return 'Snow';
    if (c >= 80 && c <= 82) return 'Showers';
    if (c === 85 || c === 86) return 'Snow showers';
    if (c >= 95) return 'Thunderstorms';
    return '';
}

export function iconFor(code, isDay = true) {
    const c = Number(code);
    if (c === 0 || c === 1) return isDay ? 'weather-clear-symbolic' : 'weather-clear-night-symbolic';
    if (c === 2) return isDay ? 'weather-few-clouds-symbolic' : 'weather-few-clouds-night-symbolic';
    if (c === 3) return 'weather-overcast-symbolic';
    if (c === 45 || c === 48) return 'weather-fog-symbolic';
    if (c >= 51 && c <= 57) return 'weather-showers-scattered-symbolic';
    if ((c >= 61 && c <= 67) || (c >= 80 && c <= 82)) return 'weather-showers-symbolic';
    if ((c >= 71 && c <= 77) || c === 85 || c === 86) return 'weather-snow-symbolic';
    if (c >= 95) return 'weather-storm-symbolic';
    return 'weather-severe-alert-symbolic';
}

export const WeatherIndicator = GObject.registerClass(
class WeatherIndicator extends PanelMenu.Button {
    _init(ext, settings) {
        super._init(0.5, 'MacBar Weather', false);
        this._ext = ext;
        this._settings = settings;
        const box = new St.BoxLayout({style_class: 'panel-status-menu-box'});
        this._icon = new St.Icon({style_class: 'system-status-icon', icon_name: 'weather-clear-symbolic'});
        this._temp = new St.Label({y_align: Clutter.ActorAlign.CENTER, style_class: 'macbar-temp'});
        box.add_child(this._icon);
        box.add_child(this._temp);
        this.add_child(box);
        this._ids = ['weather-lat', 'weather-lon', 'weather-fahrenheit', 'weather-city']
            .map(k => this._settings.connect(`changed::${k}`, () => this.refresh()));

        // Show the last known data straight away (and offline).
        try {
            const cached = this._settings.get_string('weather-cache');
            if (cached) this._render(JSON.parse(cached));
        } catch (e) { /* ignore a bad cache */ }
        this.refresh();
        this._timer = timeout(30 * 60 * 1000, () => { this.refresh(); return true; });
    }

    async refresh() {
        const lat = this._settings.get_double('weather-lat');
        const lon = this._settings.get_double('weather-lon');
        const f = this._settings.get_boolean('weather-fahrenheit');
        const url = 'https://api.open-meteo.com/v1/forecast' +
            `?latitude=${lat.toFixed(4)}&longitude=${lon.toFixed(4)}` +
            '&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day' +
            '&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=7&timezone=auto' +
            (f ? '&temperature_unit=fahrenheit&wind_speed_unit=mph' : '');
        try {
            const data = await fetchJson(url);
            data._fahrenheit = f;
            data._fetched = Date.now();
            this._settings.set_string('weather-cache', JSON.stringify(data));
            this._render(data);
        } catch (e) {
            if (!this._data) {
                this._temp.text = '';
                this._icon.icon_name = 'weather-severe-alert-symbolic';
                this._buildMenu(`Weather unavailable (${e.message})`);
            }
        }
    }

    _render(data) {
        this._data = data;
        const cur = data.current;
        if (!cur) return;
        this._icon.icon_name = iconFor(cur.weather_code, cur.is_day !== 0);
        this._temp.text = `${Math.round(cur.temperature_2m)}°`;
        this._buildMenu(null);
    }

    _buildMenu(error) {
        this.menu.removeAll();
        const city = this._settings.get_string('weather-city') || 'Set your city in MacBar Settings';
        if (error || !this._data) {
            this.menu.addMenuItem(new PopupMenu.PopupMenuItem(error || 'Loading…', {reactive: false}));
        } else {
            const d = this._data, cur = d.current, unit = d._fahrenheit ? 'mph' : 'km/h';
            const head = new PopupMenu.PopupBaseMenuItem({reactive: false, style_class: 'macbar-weather-head'});
            const col = new St.BoxLayout({vertical: true, x_expand: true});
            col.add_child(new St.Label({text: city, style_class: 'macbar-weather-city'}));
            col.add_child(new St.Label({text: `${Math.round(cur.temperature_2m)}°`, style_class: 'macbar-weather-big'}));
            col.add_child(new St.Label({text: describe(cur.weather_code), style_class: 'macbar-weather-desc'}));
            if (d.daily) col.add_child(new St.Label({
                text: `H:${Math.round(d.daily.temperature_2m_max[0])}°  L:${Math.round(d.daily.temperature_2m_min[0])}°`,
                style_class: 'macbar-weather-desc',
            }));
            head.add_child(col);
            head.add_child(new St.Icon({icon_name: iconFor(cur.weather_code, cur.is_day !== 0), icon_size: 48, y_align: Clutter.ActorAlign.CENTER}));
            this.menu.addMenuItem(head);
            this.menu.addMenuItem(new PopupMenu.PopupMenuItem(
                `Feels like ${Math.round(cur.apparent_temperature)}°  ·  Humidity ${cur.relative_humidity_2m}%  ·  Wind ${Math.round(cur.wind_speed_10m)} ${unit}`,
                {reactive: false, style_class: 'macbar-weather-detail'}));
            this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
            if (d.daily) {
                const days = d.daily.time.length;
                for (let i = 1; i < Math.min(days, 7); i++) {
                    const row = new PopupMenu.PopupBaseMenuItem({reactive: false, style_class: 'macbar-forecast-row'});
                    const date = GLib.DateTime.new_from_iso8601(`${d.daily.time[i]}T12:00:00Z`, null);
                    row.add_child(new St.Label({text: date ? date.format('%a') : d.daily.time[i], style_class: 'macbar-forecast-day', y_align: Clutter.ActorAlign.CENTER}));
                    row.add_child(new St.Icon({icon_name: iconFor(d.daily.weather_code[i]), icon_size: 16, y_align: Clutter.ActorAlign.CENTER}));
                    row.add_child(new St.Label({
                        text: `${Math.round(d.daily.temperature_2m_min[i])}°  –  ${Math.round(d.daily.temperature_2m_max[i])}°`,
                        x_expand: true, x_align: Clutter.ActorAlign.END, y_align: Clutter.ActorAlign.CENTER, style_class: 'macbar-forecast-temps',
                    }));
                    this.menu.addMenuItem(row);
                }
            }
        }
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const set = new PopupMenu.PopupMenuItem('Weather Settings…');
        set.connect('activate', () => this._ext.openPreferences());
        this.menu.addMenuItem(set);
        this.menu.addMenuItem(new PopupMenu.PopupMenuItem('Weather data by Open-Meteo.com', {reactive: false, style_class: 'macbar-credit'}));
    }

    destroy() {
        this._timer = removeSource(this._timer);
        this._ids.forEach(id => this._settings.disconnect(id));
        super.destroy();
    }
});
