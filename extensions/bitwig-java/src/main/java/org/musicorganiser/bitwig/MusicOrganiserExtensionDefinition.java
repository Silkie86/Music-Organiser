package org.musicorganiser.bitwig;

import com.bitwig.extension.api.PlatformType;
import com.bitwig.extension.controller.AutoDetectionMidiPortNamesList;
import com.bitwig.extension.controller.ControllerExtension;
import com.bitwig.extension.controller.ControllerExtensionDefinition;
import com.bitwig.extension.controller.api.ControllerHost;
import java.util.UUID;

/**
 * Tells Bitwig about the extension (META-INF/services lists this class). It keeps the JavaScript script's
 * identity, above all its ID, so Bitwig treats it as the same controller: one already added in Settings >
 * Controllers stays added, with its "Let the app make changes" setting.
 */
public final class MusicOrganiserExtensionDefinition extends ControllerExtensionDefinition {
    /** The ID the JavaScript script (extensions/bitwig/MusicOrganiser.control.js) was defined with. */
    static final UUID ID = UUID.fromString("ed35e3c7-c544-49ed-936d-c236b0c9b73b");
    static final String VERSION = "3.6";
    /**
     * API 23 (Bitwig 6.1) has everything the link uses: parameter steps and names, origins, automation, channel
     * IDs, the last clicked parameter and the master recorder. Nothing newer (and no beta method) is used.
     */
    static final int API_VERSION = 23;

    @Override
    public String getName() {
        return "Music Organiser";
    }

    @Override
    public String getAuthor() {
        return "Music Organiser";
    }

    @Override
    public String getVersion() {
        return VERSION;
    }

    @Override
    public UUID getId() {
        return ID;
    }

    @Override
    public int getRequiredAPIVersion() {
        return API_VERSION;
    }

    @Override
    public String getHardwareVendor() {
        return "Music Organiser";
    }

    @Override
    public String getHardwareModel() {
        return "DAW link";
    }

    @Override
    public int getNumMidiInPorts() {
        return 0;
    }

    @Override
    public int getNumMidiOutPorts() {
        return 0;
    }

    @Override
    public void listAutoDetectionMidiPortNames(AutoDetectionMidiPortNamesList list, PlatformType platformType) {
        // Nothing to detect: the link has no MIDI ports, so it's added by hand.
    }

    @Override
    public ControllerExtension createInstance(ControllerHost host) {
        return new MusicOrganiserExtension(this, host);
    }
}
