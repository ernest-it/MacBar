// MacBar for Ubuntu: a macOS-style menu bar and dock for GNOME Shell.
import GLib from 'gi://GLib';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

import {TopBar} from './lib/topbar.js';
import {Dock} from './lib/dock.js';
import {Workspaces} from './lib/workspaces.js';
import {disposeSession} from './lib/util.js';

// Docks that would sit under MacBar's dock. They are turned off only while MacBar is actually running.
const OTHER_DOCKS = ['ubuntu-dock@ubuntu.com', 'dash-to-dock@micxgx.gmail.com'];

export default class MacBarExtension extends Extension {
    enable() {
        this._workspaces = new Workspaces(this);
        this._topBar = new TopBar(this, this._workspaces);
        this._dock = new Dock(this);
        this._workspaces.runAtLogin();
        // Turn Ubuntu Dock off now that MacBar runs. At sign-in, wait until startup finishes: Ubuntu Dock
        // runs its own startup animation then and throws if it has been turned off before.
        const offSoon = () => {
            this._docksId = GLib.idle_add(GLib.PRIORITY_DEFAULT, () => {
                this._docksId = 0;
                this._setOtherDocks(false);
                return GLib.SOURCE_REMOVE;
            });
        };
        if (Main.layoutManager._startingUp) {
            this._docksStartupId = Main.layoutManager.connect('startup-complete', () => {
                Main.layoutManager.disconnect(this._docksStartupId);
                this._docksStartupId = 0;
                offSoon();
            });
        } else {
            offSoon();
        }
        // GNOME opens the overview at sign-in; start on the desktop instead (the way Ubuntu Dock does it).
        if (Main.layoutManager._startingUp) {
            this._hadOverview = Main.sessionMode.hasOverview;
            Main.sessionMode.hasOverview = false;
            this._startupId = Main.layoutManager.connect('startup-complete', () => this._endStartup());
        }
        // Test hook for the headless GNOME Shell used in development (see test/run.sh); does nothing otherwise.
        if (GLib.getenv('MACBAR_SELFTEST')) {
            import('./selftest.js')
                .then(m => { this._selftest = new m.SelfTest(this); })
                .catch(e => console.error(`MacBar selftest: ${e}`));
        }
    }

    _endStartup() {
        if (!this._startupId) return;
        Main.layoutManager.disconnect(this._startupId);
        this._startupId = 0;
        Main.sessionMode.hasOverview = this._hadOverview;
        // Newer GNOME already has the overview open by now, and may open it once more as startup finishes.
        Main.overview.hide();
        this._shownId = Main.overview.connect('shown', () => Main.overview.hide());
        this._shownTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 3000, () => {
            this._shownTimer = 0;
            this._stopStartupWatch();
            return GLib.SOURCE_REMOVE;
        });
    }

    _stopStartupWatch() {
        if (this._shownId) { Main.overview.disconnect(this._shownId); this._shownId = 0; }
        if (this._shownTimer) { GLib.source_remove(this._shownTimer); this._shownTimer = 0; }
    }

    _setOtherDocks(on) {
        const mgr = Main.extensionManager;
        for (const uuid of OTHER_DOCKS) {
            const ext = mgr.lookup(uuid);
            if (!ext) continue;
            const enabled = ext.state === 1 /* ACTIVE */ || ext.enabled;
            try {
                if (!on && enabled) mgr.disableExtension(uuid);
                else if (on && !enabled) mgr.enableExtension(uuid);
            } catch (e) { console.error(`MacBar: could not ${on ? 'enable' : 'disable'} ${uuid}: ${e}`); }
        }
    }

    disable() {
        if (this._docksId) { GLib.source_remove(this._docksId); this._docksId = 0; }
        if (this._docksStartupId) { Main.layoutManager.disconnect(this._docksStartupId); this._docksStartupId = 0; }
        // Turned off by the user (not just the lock screen, which disables extensions too): give Ubuntu Dock back.
        if (!Main.sessionMode.isLocked && Main.sessionMode.currentMode !== 'unlock-dialog')
            this._setOtherDocks(true);
        this._endStartup();
        this._stopStartupWatch();
        this._selftest?.destroy?.();
        this._selftest = null;
        this._dock?.destroy();
        this._dock = null;
        this._topBar?.destroy();
        this._topBar = null;
        this._workspaces?.destroy();
        this._workspaces = null;
        disposeSession();
    }

    get workspaces() { return this._workspaces; }
    get dock() { return this._dock; }
    get topBar() { return this._topBar; }
}
