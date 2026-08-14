use std::process::{Command, Stdio};

/// Zvuk kod prompta — pandan macOS `NSSound("Glass")` i Windows `SystemSounds.Asterisk`.
///
/// Linux nema jedan API za sistemski zvuk, pa redom probamo uobičajene playere iz
/// freedesktop sound teme. Prvi koji se uspije pokrenuti se zapamti; ako nijedan ne
/// postoji, prompt je i dalje potpuno funkcionalan — samo tih.
pub fn play_prompt_sound() {
    // Reprodukcija ide u zasebnom threadu: `Command::status()` čeka kraj zvuka, a GTK
    // main thread u međuvremenu mora nacrtati prompt.
    std::thread::spawn(|| {
        for (program, args) in candidates() {
            let ok = Command::new(program)
                .args(args)
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .map(|s| s.success())
                .unwrap_or(false);
            if ok {
                return;
            }
        }
    });
}

/// Postoji li ijedan player u PATH-u — postavke po tome znaju treba li upozoriti da
/// uključen zvuk neće ništa svirati.
pub fn is_available() -> bool {
    let Some(path) = std::env::var_os("PATH") else {
        return false;
    };
    candidates().iter().any(|(program, _)| {
        std::env::split_paths(&path).any(|dir| dir.join(program).is_file())
    })
}

/// `canberra-gtk-play` sam nađe zvuk u temi; ostali traže konkretnu datoteku, pa im
/// dajemo standardnu putanju iz `sound-theme-freedesktop` paketa.
fn candidates() -> [(&'static str, Vec<&'static str>); 4] {
    const OGA: &str = "/usr/share/sounds/freedesktop/stereo/message.oga";
    [
        ("canberra-gtk-play", vec!["-i", "message"]),
        ("paplay", vec![OGA]),
        ("pw-play", vec![OGA]),
        ("aplay", vec!["-q", "/usr/share/sounds/freedesktop/stereo/message.wav"]),
    ]
}
