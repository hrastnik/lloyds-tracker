use std::rc::Rc;

use gtk4::prelude::*;
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::{Align, Application, EventControllerKey, Window};

use crate::ui;
use crate::update::AvailableUpdate;

/// Pop-up koji jednom po verziji javi da je izašla nova verzija. Poslije toga obavijest
/// ostaje samo u meniju u traci.
pub struct UpdatePopup {
    window: Window,
}

impl UpdatePopup {
    pub fn new(
        app: &Application,
        update: &AvailableUpdate,
        current_version: &str,
        on_download: impl Fn() + 'static,
    ) -> Rc<Self> {
        let content = ui::vbox(16);
        content.append(&ui::brand_header(None));

        let intro = ui::vbox(6);
        intro.append(&ui::label(&format!("Nova verzija {}", update.version), &["big-title"]));
        intro.append(&ui::wrapped(
            &format!(
                "Imaš verziju {current_version}. Novu preuzmi s GitHuba i zamijeni postojeću \
                 aplikaciju. Poveznica ostaje i u meniju."
            ),
            &["muted"],
            46,
        ));
        content.append(&intro);

        // Isti prozor kao podsjetnik kod pokretanja (bez okvira, brand kartica).
        let window = Window::builder()
            .application(app)
            .title("Lloyds Tracker")
            .resizable(false)
            .decorated(false)
            .build();
        window.add_css_class("brand");

        let buttons = ui::hbox(10);
        let download_button = {
            let win = window.clone();
            ui::button("Preuzmi", &["yellow"], move || {
                win.close();
                on_download();
            })
        };
        download_button.set_hexpand(true);
        buttons.append(&download_button);

        let later_button = {
            let win = window.clone();
            ui::button("Kasnije", &["outline"], move || win.close())
        };
        buttons.append(&later_button);
        content.append(&buttons);

        content.add_css_class("brand-card");
        content.set_halign(Align::Center);
        content.set_valign(Align::Center);
        ui::pad(&content, 22);
        content.set_size_request(360, -1);

        // Esc = Kasnije.
        let key = EventControllerKey::new();
        {
            let win = window.clone();
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    win.close();
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        window.add_controller(key);

        window.set_child(Some(&content));
        ui::PanelFade::appear(&window);
        Rc::new(UpdatePopup { window })
    }

    pub fn close(&self) {
        self.window.close();
    }
}
