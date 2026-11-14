// A tiny MPRIS music player for the self-test (gjs test/fake-player.js): "plays" a track, and
// Next / Previous / PlayPause change what it reports, so the Now Playing island has something to show.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const XML = `<node><interface name="org.mpris.MediaPlayer2.Player">
  <method name="Next"/><method name="Previous"/><method name="PlayPause"/>
  <method name="SetPosition"><arg type="o" direction="in"/><arg type="x" direction="in"/></method>
  <property name="PlaybackStatus" type="s" access="read"/>
  <property name="Metadata" type="a{sv}" access="read"/>
  <property name="CanSeek" type="b" access="read"/>
  <property name="Position" type="x" access="read"/>
</interface></node>`;

const TRACKS = [['First Song', 'Test Artist'], ['Second Song', 'Other Artist']];
let track = 0, playing = true, obj;

const player = {
    get PlaybackStatus() { return playing ? 'Playing' : 'Paused'; },
    get Metadata() {
        return {
            'mpris:trackid': new GLib.Variant('o', `/test/track${track}`),
            'xesam:title': new GLib.Variant('s', TRACKS[track][0]),
            'xesam:artist': new GLib.Variant('as', [TRACKS[track][1]]),
            'mpris:length': new GLib.Variant('x', 180000000),
        };
    },
    get CanSeek() { return true; },
    get Position() { return 30000000; },
    Next() { track = (track + 1) % TRACKS.length; changed(); },
    Previous() { track = (track + TRACKS.length - 1) % TRACKS.length; changed(); },
    PlayPause() { playing = !playing; changed(); },
    SetPosition() {},
};

function changed() {
    print(`fake-player: ${TRACKS[track][0]} ${playing ? 'playing' : 'paused'}`);
    obj.emit_property_changed('PlaybackStatus', new GLib.Variant('s', player.PlaybackStatus));
    obj.emit_property_changed('Metadata', new GLib.Variant('a{sv}', player.Metadata));
    obj.flush();
}

obj = Gio.DBusExportedObject.wrapJSObject(XML, player);
obj.export(Gio.DBus.session, '/org/mpris/MediaPlayer2');
Gio.bus_own_name_on_connection(Gio.DBus.session, 'org.mpris.MediaPlayer2.macbartest', Gio.BusNameOwnerFlags.NONE, null, null);
new GLib.MainLoop(null, false).run();
