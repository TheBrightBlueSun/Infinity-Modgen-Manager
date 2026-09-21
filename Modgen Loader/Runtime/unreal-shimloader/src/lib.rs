#![allow(unused, clippy::undocumented_unsafe_blocks)]
#![warn(
    clippy::pedantic,
    clippy::unwrap_used,
)]

use std::{env, thread, fs};
use std::io::Write;
use std::alloc::GlobalAlloc;
use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::fs::{canonicalize, File};
use std::ops::Index;
use std::path::{Path, PathBuf};

use chrono::Local;
use log::{debug, error, LevelFilter};
use getargs::{Arg, Opt, Options};
use once_cell::sync::{Lazy, OnceCell};
use serde::Deserialize;
use paths::{NormalizedPath, PathRegistry, PATH_REGISTRY};
use widestring::U16CString;
use windows_sys::w;
use windows_sys::Win32::Foundation::{BOOL, HWND, TRUE};
use windows_sys::Win32::System::Console::AllocConsole;
use windows_sys::Win32::System::Diagnostics::Debug::DebugActiveProcess;
use windows_sys::Win32::System::LibraryLoader::LoadLibraryW;
use windows_sys::Win32::System::SystemServices::DLL_PROCESS_ATTACH;
use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessId};
use windows_sys::Win32::UI::WindowsAndMessaging::{MESSAGEBOX_STYLE, MessageBoxW};

mod hooks;
mod paths;
mod utils;

static GAME_ROOT: Lazy<PathBuf> = Lazy::new(|| {
    let current_exe = env::current_exe().unwrap();
    current_exe
        .ancestors()
        .nth(3)
        .unwrap_or_else(|| {
            panic!(
                "The executable at {current_exe:?} is not contained within a valid UE directory structure."
            )
        })
        .to_path_buf()
});

static EXE_DIR: Lazy<PathBuf> = Lazy::new(|| {
    env::current_exe()
        .unwrap()
        .parent()
        .unwrap()
        .to_path_buf()
});

const RUNTIME_MANIFEST_FILE_NAME: &str =
    ".infinity_modgen_mods.json";

#[derive(Debug, Deserialize)]
struct RuntimeMod {
    #[serde(rename = "ModDirectory")]
    mod_directory: String,

    #[serde(rename = "Priority")]
    priority: i32,

    #[serde(rename = "PriorityFolderName")]
    priority_folder_name: String,

    #[serde(rename = "PakFiles")]
    pak_files: Vec<String>,
}

#[no_mangle]
#[allow(non_snake_case)]
pub unsafe extern "system" fn DllMain(
    dll_module: u32,
    call_reason: u32,
    reserved: *const c_void,
) -> BOOL {
    if call_reason == DLL_PROCESS_ATTACH &&
        PATH_REGISTRY.get().is_none()
    {
        shim_init();
    }

    TRUE
}

unsafe fn shim_init() {
    #[cfg(debug_assertions)]
    AllocConsole();

    std::panic::set_hook(Box::new(|x| unsafe {
        let message =
            format!(
                "unreal-shimloader has crashed: \n\n{x}"
            );

        error!("{message}");

        let message =
            U16CString::from_str(message);

        MessageBoxW(
            0,
            message.unwrap().as_ptr(),
            w!("unreal-shimloader"),
            0,
        );
    }));

    let current_exe =
        env::current_exe()
            .expect(
                "Failed to get the path of the currently running executable."
            );

    let exe_dir =
        current_exe
            .parent()
            .expect(
                "Failed to determine the directory containing the game executable."
            );

    let target =
        Box::new(
            File::create(
                exe_dir.join("shimloader-log.txt")
            )
            .expect(
                "Failed to create log file."
            )
        );

    env_logger::Builder::new()
        .target(
            env_logger::Target::Pipe(
                target
            )
        )
        .filter(
            None,
            LevelFilter::Debug
        )
        .format(
            |buf, record| {
                writeln!(
                    buf,
                    "[{} {} {}:{}] {}",
                    Local::now()
                        .format(
                            "%Y-%m-%d %H:%M:%S%.3f"
                        ),
                    record.level(),
                    record.file()
                        .unwrap_or("unknown"),
                    record.line()
                        .unwrap_or(0),
                    record.args()
                )
            }
        )
        .init();

    debug!("unreal_shimloader -- start");

    debug!(
        "current directory: {exe_dir:?}"
    );

    debug!(
        "current executable: {current_exe:?}"
    );

    debug!(
        "args: {:?}",
        env::args().collect::<Vec<_>>()
    );

    let xinput_path =
        exe_dir.join("xinput1_3.dll");

    assert!(
        !xinput_path.exists(),
        "Shimloader is not compatible with the xinput1_3.dll UE4SS binary.\n
        1. Remove the file at {xinput_path:?} \n
        2. Ensure that ue4ss.dll exists within {exe_dir:?} \n
        3. Run the game again.",
    );

    let args =
        env::args()
            .skip(1)
            .collect::<Vec<_>>();

    let mut opts =
        Options::new(
            args.iter()
                .map(String::as_str)
        );

    let mut lua_dir: Option<PathBuf> =
        None;

    let mut pak_dir: Option<PathBuf> =
        None;

    let mut cfg_dir: Option<PathBuf> =
        None;

    let mut overlay_dir: Option<PathBuf> =
        None;

    while let Some(opt) =
        opts.next_arg()
            .expect(
                "Failed to parse arguments"
            )
    {
        match opt {
            Arg::Long("mod-dir") => {
                lua_dir =
                    Some(
                        PathBuf::from(
                            opts.value()
                                .expect(
                                    "`--mod-dir` argument has no value."
                                )
                        )
                    )
            }

            Arg::Long("pak-dir") => {
                pak_dir =
                    Some(
                        PathBuf::from(
                            opts.value()
                                .expect(
                                    "`--pak-dir` argument has no value."
                                )
                        )
                    )
            }

            Arg::Long("cfg-dir") => {
                cfg_dir =
                    Some(
                        PathBuf::from(
                            opts.value()
                                .expect(
                                    "`--cfg-dir` argument has no value."
                                )
                    )
                    )
            }

            Arg::Long("overlay-dir") => {
                overlay_dir =
                    Some(
                        PathBuf::from(
                            opts.value()
                                .expect(
                                    "`--overlay-dir` argument has no value."
                                )
                        )
                    )
            }

            _ => (),
        }
    }

    /*
     * The Manager stores its enabled-mod configuration in:
     *
     *     <GameRoot>\Mods\.infinity_modgen_mods.json
     *
     * This manifest is loaded regardless of whether the game was
     * launched with command-line configuration.
     *
     * The manifest contains ONLY enabled PAK mods.
     *
     * PAK files themselves are never copied or deployed anywhere.
     */
    let manager_mods_directory =
        current_exe
            .ancestors()
            .nth(3)
            .map(|root| root.join("Mods"));

    let mut manifest_mods:
        Option<Vec<RuntimeMod>> = None;

    if let Some(mods_directory) =
        manager_mods_directory.as_ref()
    {
        let manifest_path =
            mods_directory.join(
                RUNTIME_MANIFEST_FILE_NAME
            );

        debug!(
            "Checking runtime manifest: {manifest_path:?}"
        );

        if manifest_path.exists() {
            match fs::read_to_string(
                &manifest_path
            ) {
                Ok(json) => {
                    match serde_json::from_str::<
                        Vec<RuntimeMod>
                    >(&json) {
                        Ok(mods) => {
                            debug!(
                                "Loaded {} enabled PAK mod entries from runtime manifest.",
                                mods.len()
                            );

                            manifest_mods =
                                Some(mods);
                        }

                        Err(e) => {
                            error!(
                                "Failed to parse runtime manifest {manifest_path:?}: {e}"
                            );
                        }
                    }
                }

                Err(e) => {
                    error!(
                        "Failed to read runtime manifest {manifest_path:?}: {e}"
                    );
                }
            }
        } else {
            debug!(
                "Runtime manifest was not found."
            );
        }
    }

    /*
     * If the Manager did not supply runtime directories, use the
     * Manager's normal directories automatically.
     *
     * --pak-dir points directly at:
     *
     *     <GameRoot>\Mods
     *
     * There is deliberately NO _Deployed\Paks directory.
     */
    if lua_dir.is_none() {
        if let Some(mods_directory) =
            manager_mods_directory.as_ref()
        {
            lua_dir =
                Some(
                    mods_directory.clone()
                );
        }
    }

    if pak_dir.is_none() {
        if let Some(mods_directory) =
            manager_mods_directory.as_ref()
        {
            pak_dir =
                Some(
                    mods_directory.clone()
                );
        }
    }

    if cfg_dir.is_none() {
        if let Some(mods_directory) =
            manager_mods_directory.as_ref()
        {
            cfg_dir =
                Some(
                    mods_directory.join("Config")
                );
        }
    }

    let run_vanilla =
        ![&lua_dir, &pak_dir, &cfg_dir]
            .iter()
            .any(|x| x.is_some());

    if run_vanilla {
        debug!(
            "No Infinity Modgen runtime configuration was found; starting vanilla."
        );

        return;
    }

    let toplevel_dir =
        current_exe
            .ancestors()
            .nth(3)
            .unwrap_or_else(|| {
                panic!(
                    "The executable at {current_exe:?} is not contained within a valid UE directory structure."
                )
            });

    let mods_pak_dir =
        toplevel_dir
            .join("Content")
            .join("Paks")
            .join("~mods");

    if !mods_pak_dir.is_dir() {
        let _ =
            fs::create_dir_all(
                &mods_pak_dir
            );
    }

    let real_config_dir =
        toplevel_dir.join("Config");

    if !real_config_dir.is_dir() {
        let _ =
            fs::create_dir_all(
                &real_config_dir
            );
    }

    let ue4ss_mods =
        paths::NormalizedPath::new(
            &lua_dir.unwrap()
        );

    let bp_mods =
        paths::NormalizedPath::new(
            &pak_dir.unwrap()
        );

    let config_dir =
        paths::NormalizedPath::new(
            &cfg_dir.unwrap()
        );

    for dir in [
        ue4ss_mods.as_ref(),
        bp_mods.as_ref(),
        config_dir.as_ref()
    ] {
        let _ =
            fs::create_dir_all(dir);
    }

    env::set_var(
        "SHIMLOADER_MOD_DIR",
        ue4ss_mods.as_ref()
    );

    env::set_var(
        "SHIMLOADER_PAK_DIR",
        bp_mods.as_ref()
    );

    env::set_var(
        "SHIMLOADER_CFG_DIR",
        config_dir.as_ref()
    );

    if let Some(overlay) =
        overlay_dir.as_ref()
    {
        env::set_var(
            "SHIMLOADER_OVERLAY_DIR",
            overlay
        );
    }

    let mut registry =
        PathRegistry::new();

    if let Some(overlay) =
        overlay_dir.as_ref()
    {
        registry.register_overlay_dir(
            overlay,
            EXE_DIR.as_path()
        );
    }

    registry.register(
        EXE_DIR.join("Mods"),
        ue4ss_mods.to_path_buf()
    );

    let pak_source =
        EXE_DIR
            .join("..")
            .join("..")
            .join("Content")
            .join("Paks")
            .join("~mods");

    /*
     * Map the game's virtual ~mods directory directly to
     * the Manager's Mods directory.
     *
     * No PAK files are copied into Content\Paks\~mods.
     */
    registry.register(
        pak_source.clone(),
        bp_mods.to_path_buf()
    );

    /*
     * The runtime manifest contains only enabled mods.
     *
     * Every mod directory that is NOT represented by the manifest
     * is masked from the virtual ~mods directory.
     */
    if let Some(mods) =
        manifest_mods.as_ref()
    {
        let mut enabled_directories =
            HashSet::<String>::new();

        for mod_entry in mods {
            /*
             * ModDirectory is the actual installed directory recorded
             * by the Manager, so use its final directory component.
             *
             * PriorityFolderName is retained as a fallback for
             * compatibility with generated safe mod names.
             */
            if let Some(directory_name) =
                Path::new(
                    &mod_entry.mod_directory
                )
                .file_name()
            {
                enabled_directories.insert(
                    directory_name
                        .to_string_lossy()
                        .to_lowercase()
                );
            }

            if !mod_entry
                .priority_folder_name
                .is_empty()
            {
                enabled_directories.insert(
                    mod_entry
                        .priority_folder_name
                        .to_lowercase()
                );
            }
        }

        let manager_mods_path =
            PathBuf::from(
                bp_mods.as_ref()
            );

        debug!(
            "Checking Manager mod directory for disabled mods: {manager_mods_path:?}"
        );

        if let Ok(entries) =
            fs::read_dir(
                &manager_mods_path
            )
        {
            for entry in
                entries.filter_map(Result::ok)
            {
                let path =
                    entry.path();

                if !path.is_dir() {
                    continue;
                }

                let directory_name =
                    match path.file_name() {
                        Some(name) =>
                            name
                                .to_string_lossy()
                                .to_lowercase(),

                        None =>
                            continue,
                    };

                if !enabled_directories
                    .contains(
                        &directory_name
                    )
                {
                    let virtual_mod_directory =
                        pak_source.join(
                            path.file_name()
                                .unwrap()
                        );

                    registry.register_mask(
                        virtual_mod_directory
                            .clone()
                    );

                    debug!(
                        "Masked disabled mod directory: {:?}",
                        path
                    );
                } else {
                    debug!(
                        "Enabled mod directory remains visible: {:?}",
                        path
                    );
                }
            }
        }
    }

    let config_source =
        EXE_DIR
            .join("..")
            .join("..")
            .join("Config");

    registry.register(
        config_source,
        config_dir.to_path_buf()
    );

    let _ =
        PATH_REGISTRY.set(
            registry
        );

    if let Err(e) =
        hooks::enable_hooks()
    {
        panic!(
            "Failed to enable one or more hooks. {e}"
        )
    }

    load_ue4ss(
        &current_exe
    );
}

unsafe fn load_ue4ss(
    current_exe: &Path
) {
    let exe_dir =
        current_exe
            .parent()
            .expect(
                "Could not determine the directory containing the game executable."
            );

    let ue4ss_dll =
        exe_dir.join(
            "ue4ss.dll"
        );

    assert!(
        ue4ss_dll.is_file(),
        "ue4ss.dll could not be found at {ue4ss_dll:?}"
    );

    let wide_path =
        U16CString::from_str(
            ue4ss_dll
                .to_str()
                .expect(
                    "ue4ss.dll path contains invalid characters."
                )
        )
        .expect(
            "Failed to convert ue4ss.dll path to UTF-16."
        );

    LoadLibraryW(
        wide_path.as_ptr()
    );
}