use std::alloc::Layout;
use std::collections::HashMap;
use std::error::Error;
use std::ffi::c_void;
use std::fs;
use std::path::{Path, PathBuf};
use std::ptr;
use std::slice;
use std::sync::Mutex;

use log::{debug, error};
use once_cell::sync::Lazy;
use retour::static_detour;
use widestring::{U16CStr, U16CString, WideString};
use windows_sys::core::{PCWSTR, PWSTR};
use windows_sys::Win32::Foundation::{
    SetLastError,
    BOOL,
    ERROR_FILE_NOT_FOUND,
    ERROR_NO_MORE_FILES,
    FILETIME,
    HANDLE,
    INVALID_HANDLE_VALUE,
    MAX_PATH,
    NTSTATUS,
    STATUS_OBJECT_NAME_NOT_FOUND,
    HMODULE,
    UNICODE_STRING,
};
use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW,
    FindClose,
    FindFileHandle,
    FindFirstFileExW,
    FindFirstFileW,
    FindNextFileW,
    GetFileAttributesExW,
    GetFileAttributesW,
    NtCreateFile,
    FILE_ATTRIBUTE_DIRECTORY,
    FILE_CREATION_DISPOSITION,
    FILE_FLAGS_AND_ATTRIBUTES,
    FILE_SHARE_MODE,
    FINDEX_INFO_LEVELS,
    FINDEX_SEARCH_OPS,
    FIND_FIRST_EX_FLAGS,
    GET_FILEEX_INFO_LEVELS,
    NT_CREATE_FILE_DISPOSITION,
    WIN32_FIND_DATAW,
};
use windows_sys::Win32::System::Environment::GetCommandLineW;
use windows_sys::Win32::System::LibraryLoader::{
    AddDllDirectory,
    GetModuleFileNameW,
    LoadLibraryExW,
    LoadLibraryW,
    LOAD_LIBRARY_FLAGS,
};
use windows_sys::Win32::System::WindowsProgramming::{
    IO_STATUS_BLOCK,
    OBJECT_ATTRIBUTES,
};

use crate::paths::{
    self,
    is_masked,
    remap_path,
    reverse_remap,
    NormalizedPath,
};

const INVALID_FILE_ATTRIBUTES: u32 = 0xFFFF_FFFF;

const RUNTIME_MANIFEST_FILE_NAME: &str =
    ".infinity_modgen_mods.json";

static_detour! {
    pub static CreateFileW_Detour: unsafe extern "system" fn(
        PCWSTR,
        u32,
        FILE_SHARE_MODE,
        *const SECURITY_ATTRIBUTES,
        FILE_CREATION_DISPOSITION,
        FILE_FLAGS_AND_ATTRIBUTES,
        HANDLE
    ) -> HANDLE;

    pub static NtCreateFile_Detour: unsafe extern "system" fn(
        *mut HANDLE,
        u32,
        *mut OBJECT_ATTRIBUTES,
        *mut IO_STATUS_BLOCK,
        *mut i64,
        u32,
        FILE_SHARE_MODE,
        NT_CREATE_FILE_DISPOSITION,
        u32,
        *mut c_void,
        u32
    ) -> NTSTATUS;

    pub static GetFileAttributesW_Detour: unsafe extern "system" fn(
        PCWSTR
    ) -> u32;

    pub static GetFileAttributesExW_Detour: unsafe extern "system" fn(
        PCWSTR,
        GET_FILEEX_INFO_LEVELS,
        *mut c_void
    ) -> BOOL;

    pub static FindFirstFileW_Detour: unsafe extern "system" fn(
        PCWSTR,
        *mut WIN32_FIND_DATAW
    ) -> FindFileHandle;

    pub static FindFirstFileExW_Detour: unsafe extern "system" fn(
        PCWSTR,
        FINDEX_INFO_LEVELS,
        *mut c_void,
        FINDEX_SEARCH_OPS,
        *const c_void,
        FIND_FIRST_EX_FLAGS
    ) -> FindFileHandle;

    pub static FindNextFileW_Detour: unsafe extern "system" fn(
        FindFileHandle,
        *mut WIN32_FIND_DATAW
    ) -> BOOL;

    pub static FindClose_Detour: unsafe extern "system" fn(
        HANDLE
    ) -> BOOL;

    pub static LoadLibraryW_Detour: unsafe extern "system" fn(
        PCWSTR
    ) -> HMODULE;

    pub static LoadLibraryExW_Detour: unsafe extern "system" fn(
        PCWSTR,
        HANDLE,
        LOAD_LIBRARY_FLAGS
    ) -> HMODULE;

    pub static AddDllDirectory_Detour: unsafe extern "system" fn(
        PCWSTR
    ) -> *mut c_void;

    pub static GetCommandLineW_Detour: unsafe extern "system" fn()
        -> PCWSTR;

    pub static GetModuleFileNameW_Detour: unsafe extern "system" fn(
        HMODULE,
        PWSTR,
        u32
    ) -> u32;
}

/// Switches consumed by the shimloader itself.
const SHIMLOADER_SWITCHES: &[&str] = &[
    "--mod-dir",
    "--pak-dir",
    "--cfg-dir",
    "--overlay-dir",
];

/// Cached, sanitized copy of the process command line.
static SANITIZED_COMMAND_LINE: Lazy<U16CString> = Lazy::new(|| unsafe {
    let original_ptr =
        GetCommandLineW_Detour.call();

    let original =
        U16CStr::from_ptr_str(
            original_ptr
        )
        .as_slice();

    let cleaned =
        sanitize_command_line(
            original
        );

    U16CString::from_vec_truncate(
        cleaned
    )
});

/// Enabled PAK files exposed through the virtual:
///
///     Content\Paks\~mods
///
/// The key is the filename Unreal sees.
/// The value is the real PAK/UCAS/UTOC file on disk.
static VIRTUAL_PAK_FILES: Lazy<
    Mutex<HashMap<String, PathBuf>>
> = Lazy::new(|| {
    Mutex::new(
        HashMap::new()
    )
});

/// Per-handle state for virtual ~mods enumeration.
///
/// Windows still provides a genuine search handle backed by the
/// Manager's Mods directory, but the results returned to Unreal
/// are the enabled PAK files from the individual mod folders.
static VIRTUAL_PAK_SEARCH_HANDLES: Lazy<
    Mutex<HashMap<isize, VirtualPakSearch>>
> = Lazy::new(|| {
    Mutex::new(
        HashMap::new()
    )
});

struct VirtualPakSearch {
    search_pattern: String,
    virtual_files: Vec<PathBuf>,
    virtual_index: usize,
}

/// Per-handle state for ordinary masked-entry filtering.
static SEARCH_HANDLES: Lazy<
    Mutex<HashMap<isize, NormalizedPath>>
> = Lazy::new(|| {
    Mutex::new(
        HashMap::new()
    )
});

#[derive(Debug, serde::Deserialize)]
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

pub unsafe fn enable_hooks() -> Result<(), Box<dyn Error>> {
    CreateFileW_Detour.initialize(
        CreateFileW,
        |a, b, c, d, e, f, g| unsafe {
            createfilew_detour(
                a,
                b,
                c,
                d,
                e,
                f,
                g
            )
        }
    )?.enable()?;

    NtCreateFile_Detour.initialize(
        NtCreateFile,
        |a, b, c, d, e, f, g, h, i, j, k| {
            ntcreatefile_detour(
                a,
                b,
                c,
                d,
                e,
                f,
                g,
                h,
                i,
                j,
                k
            )
        }
    )?.enable()?;

    GetFileAttributesW_Detour.initialize(
        GetFileAttributesW,
        |a| unsafe {
            getfileattributesw_detour(a)
        }
    )?.enable()?;

    GetFileAttributesExW_Detour.initialize(
        GetFileAttributesExW,
        |a, b, c| unsafe {
            getfileattributesexw_detour(
                a,
                b,
                c
            )
        }
    )?.enable()?;

    FindFirstFileW_Detour.initialize(
        FindFirstFileW,
        |a, b| unsafe {
            findfirstfilew_detour(
                a,
                b
            )
        }
    )?.enable()?;

    FindFirstFileExW_Detour.initialize(
        FindFirstFileExW,
        |a, b, c, d, e, f| unsafe {
            findfirstfileexw_detour(
                a,
                b,
                c,
                d,
                e,
                f
            )
        }
    )?.enable()?;

    FindNextFileW_Detour.initialize(
        FindNextFileW,
        |a, b| unsafe {
            findnextfilew_detour(
                a,
                b
            )
        }
    )?.enable()?;

    FindClose_Detour.initialize(
        FindClose,
        |a| unsafe {
            findclose_detour(a)
        }
    )?.enable()?;

    LoadLibraryW_Detour.initialize(
        LoadLibraryW,
        |lpfilename| unsafe {
            loadlibraryw_detour(
                lpfilename
            )
        }
    )?.enable()?;

    LoadLibraryExW_Detour.initialize(
        LoadLibraryExW,
        |lpfilename, hfile, dwflags| unsafe {
            loadlibraryexw_detour(
                lpfilename,
                hfile,
                dwflags
            )
        }
    )?.enable()?;

    AddDllDirectory_Detour.initialize(
        AddDllDirectory,
        |lppathnamestr| unsafe {
            adddlldirectory_detour(
                lppathnamestr
            )
        }
    )?.enable()?;

    GetCommandLineW_Detour.initialize(
        GetCommandLineW,
        || unsafe {
            getcommandlinew_detour()
        }
    )?.enable()?;

    GetModuleFileNameW_Detour.initialize(
        GetModuleFileNameW,
        |h, b, s| unsafe {
            getmodulefilenamew_detour(
                h,
                b,
                s
            )
        }
    )?.enable()?;

    /*
     * Build the virtual PAK table after all hooks have
     * been initialized.
     */
    load_virtual_pak_files();

    Ok(())
}

fn virtual_pak_root() -> Option<NormalizedPath> {
    let current_exe =
        std::env::current_exe().ok()?;

    let root =
        current_exe
            .ancestors()
            .nth(3)?
            .to_path_buf();

    Some(
        NormalizedPath::new(
            root
                .join("Content")
                .join("Paks")
                .join("~mods")
        )
    )
}

fn virtual_pak_source_directory() -> Option<PathBuf> {
    let current_exe =
        std::env::current_exe().ok()?;

    let root =
        current_exe
            .ancestors()
            .nth(3)?
            .to_path_buf();

    Some(
        root.join("Mods")
    )
}

fn load_virtual_pak_files() {
    let Some(mods_directory) =
        virtual_pak_source_directory()
    else {
        return;
    };

    let manifest_path =
        mods_directory.join(
            RUNTIME_MANIFEST_FILE_NAME
        );

    debug!(
        "[virtual pak] loading manifest: {:?}",
        manifest_path
    );

    let json =
        match fs::read_to_string(
            &manifest_path
        ) {
            Ok(value) => value,

            Err(error) => {
                debug!(
                    "[virtual pak] manifest could not be read: {error}"
                );

                return;
            }
        };

    let mut runtime_mods =
        match serde_json::from_str::<Vec<RuntimeMod>>(
            &json
        ) {
            Ok(value) => value,

            Err(error) => {
                error!(
                    "[virtual pak] failed to parse manifest: {error}"
                );

                return;
            }
        };

    /*
     * Lower priority values are loaded first.
     */
    runtime_mods.sort_by_key(
        |entry| entry.priority
    );

    let mut virtual_files =
        VIRTUAL_PAK_FILES
            .lock()
            .unwrap();

    virtual_files.clear();

    for runtime_mod in runtime_mods {
        for pak_file in runtime_mod.pak_files {
            let actual_path =
                PathBuf::from(
                    &pak_file
                );

            if !actual_path.is_file() {
                debug!(
                    "[virtual pak] PAK does not exist, skipping: {:?}",
                    actual_path
                );

                continue;
            }

            let Some(file_name) =
                actual_path.file_name()
            else {
                continue;
            };

            let file_name =
                file_name
                    .to_string_lossy()
                    .to_string();

            let extension =
                actual_path
                    .extension()
                    .and_then(
                        |x| x.to_str()
                    )
                    .unwrap_or("");

            if !is_pak_related_extension(
                extension
            ) {
                continue;
            }

            let key =
                file_name.to_lowercase();

            /*
             * If two enabled mods expose the same filename,
             * preserve the Manager's load-order decision.
             */
            if virtual_files.contains_key(
                &key
            ) {
                debug!(
                    "[virtual pak] filename collision, keeping earlier entry: {:?}",
                    file_name
                );

                continue;
            }

            debug!(
                "[virtual pak] exposing {:?} -> {:?}",
                file_name,
                actual_path
            );

            virtual_files.insert(
                key,
                actual_path
            );
        }
    }

    debug!(
        "[virtual pak] loaded {} virtual PAK files",
        virtual_files.len()
    );
}

fn is_pak_related_extension(
    extension: &str
) -> bool {
    extension.eq_ignore_ascii_case("pak")
        || extension.eq_ignore_ascii_case("ucas")
        || extension.eq_ignore_ascii_case("utoc")
}

fn virtual_pak_file_for_path(
    path: &NormalizedPath
) -> Option<PathBuf> {
    let root =
        virtual_pak_root()?;

    if !path.starts_with(
        &root
    ) {
        return None;
    }

    let relative =
        path.strip_prefix(
            &root
        )?;

    /*
     * Only direct children of ~mods are virtual files.
     */
    if relative.components().count() != 1 {
        return None;
    }

    let file_name =
        relative
            .file_name()
            .map(
                |x| {
                    x.to_string_lossy()
                        .to_lowercase()
                }
            )?;

    VIRTUAL_PAK_FILES
        .lock()
        .unwrap()
        .get(
            &file_name
        )
        .cloned()
}

unsafe fn set_find_data_from_file(
    find_file_data: *mut WIN32_FIND_DATAW,
    actual_file: &Path
) -> bool {
    if find_file_data.is_null() {
        return false;
    }

    /*
     * Query the real file directly through the trampoline.
     * This path is inside Mods, not the virtual ~mods directory.
     */
    let wide_path =
        paths::path_to_widestring(
            actual_file
        );

    let temporary_handle =
        FindFirstFileW_Detour.call(
            wide_path.as_ptr(),
            find_file_data
        );

    if temporary_handle ==
        INVALID_HANDLE_VALUE
    {
        return false;
    }

    FindClose_Detour.call(
        temporary_handle
    );

    true
}

fn wildcard_match(
    pattern: &str,
    value: &str
) -> bool {
    let pattern =
        pattern.to_lowercase();

    let value =
        value.to_lowercase();

    let pattern =
        pattern.as_bytes();

    let value =
        value.as_bytes();

    let mut p = 0usize;
    let mut v = 0usize;

    let mut star =
        None::<usize>;

    let mut star_value =
        0usize;

    while v < value.len() {
        if p < pattern.len()
            && (
                pattern[p] == b'?'
                    || pattern[p] == value[v]
            )
        {
            p += 1;
            v += 1;
            continue;
        }

        if p < pattern.len()
            && pattern[p] == b'*'
        {
            star =
                Some(p);

            p += 1;
            star_value =
                v;

            continue;
        }

        if let Some(star_position) =
            star
        {
            p =
                star_position + 1;

            star_value += 1;
            v =
                star_value;

            continue;
        }

        return false;
    }

    while p < pattern.len()
        && pattern[p] == b'*'
    {
        p += 1;
    }

    p == pattern.len()
}

unsafe fn advance_virtual_pak_search(
    handle: FindFileHandle,
    find_file_data: *mut WIN32_FIND_DATAW
) -> bool {
    loop {
        let actual_file =
            {
                let mut handles =
                    VIRTUAL_PAK_SEARCH_HANDLES
                        .lock()
                        .unwrap();

                let Some(state) =
                    handles.get_mut(
                        &handle
                    )
                else {
                    return false;
                };

                let mut next =
                    None;

                while state.virtual_index
                    < state.virtual_files.len()
                {
                    let candidate =
                        state.virtual_files[
                            state.virtual_index
                        ]
                        .clone();

                    state.virtual_index += 1;

                    let Some(file_name) =
                        candidate.file_name()
                    else {
                        continue;
                    };

                    let file_name =
                        file_name
                            .to_string_lossy();

                    if wildcard_match(
                        &state.search_pattern,
                        &file_name
                    ) {
                        next =
                            Some(candidate);

                        break;
                    }
                }

                next
            };

        let Some(actual_file) =
            actual_file
        else {
            return false;
        };

        if set_find_data_from_file(
            find_file_data,
            &actual_file
        ) {
            return true;
        }
    }
}

fn is_virtual_pak_enumeration(
    path: &NormalizedPath
) -> bool {
    let Some(root) =
        virtual_pak_root()
    else {
        return false;
    };

    path.original()
        .parent()
        .is_some_and(
            |parent| {
                NormalizedPath::new(parent)
                    == root
            }
        )
}

unsafe extern "system" fn findfirstfilew_detour(
    raw_file_name: PCWSTR,
    find_file_data: *mut WIN32_FIND_DATAW,
) -> FindFileHandle {
    let path =
        paths::pcwstr_to_path(
            raw_file_name
        );

    if is_virtual_pak_enumeration(
        &path
    ) {
        let Some(mods_directory) =
            virtual_pak_source_directory()
        else {
            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        };

        let search_pattern =
            path.original()
                .file_name()
                .map(
                    |x| x.to_string_lossy()
                )
                .unwrap_or_else(
                    || "*.*".into()
                )
                .to_string();

        let physical_search =
            mods_directory.join(
                "*.*"
            );

        let wide_pattern =
            paths::path_to_widestring(
                &physical_search
            );

        debug!(
            "[virtual pak] FindFirst {:?} -> {:?}",
            path,
            physical_search
        );

        let handle =
            FindFirstFileW_Detour.call(
                wide_pattern.as_ptr(),
                find_file_data
            );

        if handle ==
            INVALID_HANDLE_VALUE
        {
            return handle;
        }

        VIRTUAL_PAK_SEARCH_HANDLES
            .lock()
            .unwrap()
            .insert(
                handle,
                VirtualPakSearch {
                    search_pattern,
                    virtual_files:
                        get_virtual_pak_file_list(),
                    virtual_index:
                        0
                }
            );

        if !advance_virtual_pak_search(
            handle,
            find_file_data
        ) {
            VIRTUAL_PAK_SEARCH_HANDLES
                .lock()
                .unwrap()
                .remove(
                    &handle
                );

            FindClose_Detour.call(
                handle
            );

            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        }

        return handle;
    }

    if is_masked(&path) {
        debug!(
            "[findfirstfilew_detour] masked: {:?}",
            path
        );

        SetLastError(
            ERROR_FILE_NOT_FOUND
        );

        return INVALID_HANDLE_VALUE;
    }

    let new_path =
        remap_path(
            &path
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    debug!(
        "[findfirstfilew_detour] {:?} to {:?}",
        path,
        new_path
    );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    let raw_path =
        if path.to_path_buf()
            == new_path
        {
            raw_file_name
        } else {
            wide_path.as_ptr()
        };

    let handle =
        FindFirstFileW_Detour.call(
            raw_path,
            find_file_data
        );

    if handle ==
        INVALID_HANDLE_VALUE
    {
        return handle;
    }

    if let Some(search_dir) =
        search_dir_for_pattern(
            &path
        ) {
        if !advance_past_masked(
            handle,
            find_file_data,
            &search_dir
        ) {
            FindClose_Detour.call(
                handle
            );

            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        }

        SEARCH_HANDLES
            .lock()
            .unwrap()
            .insert(
                handle,
                search_dir
            );
    }

    handle
}

unsafe extern "system" fn findfirstfileexw_detour(
    raw_file_name: PCWSTR,
    info_level_id: FINDEX_INFO_LEVELS,
    find_file_data: *mut c_void,
    search_op: FINDEX_SEARCH_OPS,
    search_filter: *const c_void,
    additional_flags: FIND_FIRST_EX_FLAGS
) -> FindFileHandle {
    let path =
        paths::pcwstr_to_path(
            raw_file_name
        );

    if is_virtual_pak_enumeration(
        &path
    ) {
        let Some(mods_directory) =
            virtual_pak_source_directory()
        else {
            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        };

        let search_pattern =
            path.original()
                .file_name()
                .map(
                    |x| x.to_string_lossy()
                )
                .unwrap_or_else(
                    || "*.*".into()
                )
                .to_string();

        let physical_search =
            mods_directory.join(
                "*.*"
            );

        let wide_pattern =
            paths::path_to_widestring(
                &physical_search
            );

        debug!(
            "[virtual pak] FindFirstEx {:?} -> {:?}",
            path,
            physical_search
        );

        let handle =
            FindFirstFileExW_Detour.call(
                wide_pattern.as_ptr(),
                info_level_id,
                find_file_data,
                search_op,
                search_filter,
                additional_flags
            );

        if handle ==
            INVALID_HANDLE_VALUE
        {
            return handle;
        }

        VIRTUAL_PAK_SEARCH_HANDLES
            .lock()
            .unwrap()
            .insert(
                handle,
                VirtualPakSearch {
                    search_pattern,
                    virtual_files:
                        get_virtual_pak_file_list(),
                    virtual_index:
                        0
                }
            );

        let win32_data =
            find_file_data
                .cast::<WIN32_FIND_DATAW>();

        if !advance_virtual_pak_search(
            handle,
            win32_data
        ) {
            VIRTUAL_PAK_SEARCH_HANDLES
                .lock()
                .unwrap()
                .remove(
                    &handle
                );

            FindClose_Detour.call(
                handle
            );

            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        }

        return handle;
    }

    if is_masked(&path) {
        debug!(
            "[findfirstfileexw_detour] masked: {:?}",
            path
        );

        SetLastError(
            ERROR_FILE_NOT_FOUND
        );

        return INVALID_HANDLE_VALUE;
    }

    let new_path =
        remap_path(
            &path
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    debug!(
        "[findfirstfileexw_detour] {:?} to {:?}",
        path,
        new_path
    );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    let handle =
        FindFirstFileExW_Detour.call(
            wide_path.as_ptr(),
            info_level_id,
            find_file_data,
            search_op,
            search_filter,
            additional_flags
        );

    if handle ==
        INVALID_HANDLE_VALUE
    {
        return handle;
    }

    if let Some(search_dir) =
        search_dir_for_pattern(
            &path
        ) {
        let win32_data =
            find_file_data
                .cast::<WIN32_FIND_DATAW>();

        if !advance_past_masked(
            handle,
            win32_data,
            &search_dir
        ) {
            FindClose_Detour.call(
                handle
            );

            SetLastError(
                ERROR_FILE_NOT_FOUND
            );

            return INVALID_HANDLE_VALUE;
        }

        SEARCH_HANDLES
            .lock()
            .unwrap()
            .insert(
                handle,
                search_dir
            );
    }

    handle
}

unsafe extern "system" fn findnextfilew_detour(
    handle: FindFileHandle,
    find_file_data: *mut WIN32_FIND_DATAW,
) -> BOOL {
    let is_virtual =
        VIRTUAL_PAK_SEARCH_HANDLES
            .lock()
            .unwrap()
            .contains_key(
                &handle
            );

    if is_virtual {
        if advance_virtual_pak_search(
            handle,
            find_file_data
        ) {
            return 1;
        }

        SetLastError(
            ERROR_NO_MORE_FILES
        );

        return 0;
    }

    let result =
        FindNextFileW_Detour.call(
            handle,
            find_file_data
        );

    if result == 0 {
        return result;
    }

    let search_dir =
        SEARCH_HANDLES
            .lock()
            .unwrap()
            .get(
                &handle
            )
            .cloned();

    if let Some(search_dir) =
        search_dir {
        if !advance_past_masked(
            handle,
            find_file_data,
            &search_dir
        ) {
            SetLastError(
                ERROR_NO_MORE_FILES
            );

            return 0;
        }
    }

    1
}

unsafe extern "system" fn findclose_detour(
    handle: HANDLE
) -> BOOL {
    VIRTUAL_PAK_SEARCH_HANDLES
        .lock()
        .unwrap()
        .remove(
            &handle
        );

    SEARCH_HANDLES
        .lock()
        .unwrap()
        .remove(
            &handle
        );

    FindClose_Detour.call(
        handle
    )
}

unsafe extern "system" fn createfilew_detour(
    raw_file_name: PCWSTR,
    desired_access: u32,
    share_mode: FILE_SHARE_MODE,
    security_attributes: *const SECURITY_ATTRIBUTES,
    creation_disposition: FILE_CREATION_DISPOSITION,
    flags_attributes: FILE_FLAGS_AND_ATTRIBUTES,
    template_file: HANDLE,
) -> HANDLE {
    let path =
        paths::pcwstr_to_path(
            raw_file_name
        );

    if is_masked(&path) {
        debug!(
            "[createfilew_detour] masked: {:?}",
            path
        );

        SetLastError(
            ERROR_FILE_NOT_FOUND
        );

        return INVALID_HANDLE_VALUE;
    }

    let new_path =
        virtual_pak_file_for_path(
            &path
        )
        .or_else(
            || remap_path(&path)
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    debug!(
        "[createfilew_detour] {:?} to {:?}",
        path,
        new_path
    );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    CreateFileW_Detour.call(
        wide_path.as_ptr(),
        desired_access,
        share_mode,
        security_attributes,
        creation_disposition,
        flags_attributes,
        template_file
    )
}

pub unsafe extern "system" fn ntcreatefile_detour(
    file_handle: *mut HANDLE,
    desired_access: u32,
    object_attrs: *mut OBJECT_ATTRIBUTES,
    io_status_block: *mut IO_STATUS_BLOCK,
    allocation_size: *mut i64,
    file_attrs: u32,
    share_access: FILE_SHARE_MODE,
    creation_disposition: NT_CREATE_FILE_DISPOSITION,
    create_options: u32,
    ea_buffer: *mut c_void,
    ea_length: u32,
) -> NTSTATUS {
    let unicode_path =
        *(*object_attrs).ObjectName;

    let path_len =
        (unicode_path.Length / 2) as usize;

    if path_len < 4 {
        return NtCreateFile_Detour.call(
            file_handle,
            desired_access,
            object_attrs,
            io_status_block,
            allocation_size,
            file_attrs,
            share_access,
            creation_disposition,
            create_options,
            ea_buffer,
            ea_length
        );
    }

    let og_prefix =
        slice::from_raw_parts(
            unicode_path.Buffer,
            4
        );

    let offset_path =
        unicode_path.Buffer.add(4);

    let slice =
        slice::from_raw_parts(
            offset_path,
            path_len - 4
        );

    let null_pos =
        slice.iter().position(
            |&c| c == 0
        );

    let effective_len =
        null_pos.unwrap_or(
            path_len - 4
        );

    let effective_slice =
        &slice[..effective_len];

    let wide_string =
        WideString::from_vec(
            effective_slice.to_vec()
        );

    let original_path_result =
        wide_string.to_string();

    if original_path_result.is_err() {
        return NtCreateFile_Detour.call(
            file_handle,
            desired_access,
            object_attrs,
            io_status_block,
            allocation_size,
            file_attrs,
            share_access,
            creation_disposition,
            create_options,
            ea_buffer,
            ea_length
        );
    }

    let original_path_str =
        original_path_result.unwrap();

    let bad_path_prefixes =
        [
            "\\\\device",
            "c:\\windows"
        ];

    let lowercase =
        original_path_str.to_lowercase();

    if bad_path_prefixes.iter().any(
        |x| lowercase.starts_with(
            &x.to_lowercase()
        )
    ) {
        return NtCreateFile_Detour.call(
            file_handle,
            desired_access,
            object_attrs,
            io_status_block,
            allocation_size,
            file_attrs,
            share_access,
            creation_disposition,
            create_options,
            ea_buffer,
            ea_length
        );
    }

    let original_path =
        PathBuf::from(
            original_path_str
        );

    let normalized_path =
        NormalizedPath::new(
            &original_path
        );

    if is_masked(
        &normalized_path
    ) {
        debug!(
            "[ntcreatefile_detour] masked: {:?}",
            original_path
        );

        return STATUS_OBJECT_NAME_NOT_FOUND;
    }

    let new_path =
        virtual_pak_file_for_path(
            &normalized_path
        )
        .or_else(
            || remap_path(&normalized_path)
        )
        .unwrap_or_else(
            || normalized_path.to_path_buf()
        );

    debug!(
        "[ntcreatefile_detour] {:?} to {:?}",
        original_path,
        new_path
    );

    let wide_new_path =
        paths::path_to_widestring(
            &new_path
        );

    let buffer_layout =
        Layout::array::<u16>(
            og_prefix.len()
                + wide_new_path.len()
                + 1
        )
        .unwrap();

    let buffer =
        std::alloc::alloc_zeroed(
            buffer_layout
        )
        .cast::<u16>();

    let used_size =
        (
            og_prefix.len()
                + wide_new_path.len()
        ) * 2;

    let buffer_size =
        used_size + 2;

    ptr::copy_nonoverlapping(
        og_prefix.as_ptr(),
        buffer,
        og_prefix.len()
    );

    ptr::copy_nonoverlapping(
        wide_new_path.as_ptr(),
        buffer.add(
            og_prefix.len()
        ),
        wide_new_path.len()
    );

    let mut new_unicode =
        UNICODE_STRING {
            Length: used_size as _,
            MaximumLength: buffer_size as _,
            Buffer: buffer,
        };

    (*object_attrs).ObjectName =
        ptr::addr_of_mut!(
            new_unicode
        );

    NtCreateFile_Detour.call(
        file_handle,
        desired_access,
        object_attrs,
        io_status_block,
        allocation_size,
        file_attrs,
        share_access,
        creation_disposition,
        create_options,
        ea_buffer,
        ea_length
    )
}

unsafe extern "system" fn getfileattributesw_detour(
    raw_file_name: PCWSTR
) -> u32 {
    let path =
        paths::pcwstr_to_path(
            raw_file_name
        );

    if is_masked(&path) {
        debug!(
            "[getfileattributesw_detour] masked: {:?}",
            path
        );

        SetLastError(
            ERROR_FILE_NOT_FOUND
        );

        return INVALID_FILE_ATTRIBUTES;
    }

    let new_path =
        virtual_pak_file_for_path(
            &path
        )
        .or_else(
            || remap_path(&path)
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    GetFileAttributesW_Detour.call(
        wide_path.as_ptr()
    )
}

unsafe extern "system" fn getfileattributesexw_detour(
    raw_file_name: PCWSTR,
    info_level_id: GET_FILEEX_INFO_LEVELS,
    file_information: *mut c_void,
) -> BOOL {
    let path =
        paths::pcwstr_to_path(
            raw_file_name
        );

    if is_masked(&path) {
        debug!(
            "[getfileattributesexw_detour] masked: {:?}",
            path
        );

        SetLastError(
            ERROR_FILE_NOT_FOUND
        );

        return 0;
    }

    let new_path =
        virtual_pak_file_for_path(
            &path
        )
        .or_else(
            || remap_path(&path)
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    GetFileAttributesExW_Detour.call(
        wide_path.as_ptr(),
        info_level_id,
        file_information
    )
}

unsafe fn read_find_data_filename(
    find_data: *const WIN32_FIND_DATAW
) -> String {
    if find_data.is_null() {
        return String::new();
    }

    let buf =
        (*find_data).cFileName;

    let len =
        buf.iter()
            .position(
                |&c| c == 0
            )
            .unwrap_or(
                buf.len()
            );

    String::from_utf16_lossy(
        &buf[..len]
    )
}

fn search_dir_for_pattern(
    pattern: &NormalizedPath
) -> Option<NormalizedPath> {
    pattern
        .original()
        .parent()
        .map(
            NormalizedPath::new
        )
}

unsafe fn advance_past_masked(
    handle: HANDLE,
    find_file_data: *mut WIN32_FIND_DATAW,
    search_dir: &NormalizedPath,
) -> bool {
    loop {
        let name =
            read_find_data_filename(
                find_file_data
            );

        if name.is_empty()
            || name == "."
            || name == ".."
        {
            return true;
        }

        let candidate =
            search_dir.join(
                &name
            );

        if !is_masked(
            &candidate
        ) {
            return true;
        }

        debug!(
            "[find filter] masked entry skipped: {:?}",
            candidate
        );

        if FindNextFileW_Detour.call(
            handle,
            find_file_data
        ) == 0 {
            return false;
        }
    }
}

fn get_virtual_pak_file_list()
    -> Vec<PathBuf>
{
    VIRTUAL_PAK_FILES
        .lock()
        .unwrap()
        .values()
        .cloned()
        .collect()
}

unsafe extern "system" fn loadlibraryw_detour(
    lpfilename: PCWSTR
) -> HMODULE {
    let path =
        paths::pcwstr_to_path(
            lpfilename
        );

    let new_path =
        virtual_pak_file_for_path(
            &path
        )
        .or_else(
            || remap_path(&path)
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    LoadLibraryW_Detour.call(
        wide_path.as_ptr()
    )
}

unsafe extern "system" fn loadlibraryexw_detour(
    lpfilename: PCWSTR,
    hfile: HANDLE,
    dwflags: LOAD_LIBRARY_FLAGS
) -> HMODULE {
    let path =
        paths::pcwstr_to_path(
            lpfilename
        );

    let new_path =
        virtual_pak_file_for_path(
            &path
        )
        .or_else(
            || remap_path(&path)
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    LoadLibraryExW_Detour.call(
        wide_path.as_ptr(),
        hfile,
        dwflags
    )
}

unsafe extern "system" fn adddlldirectory_detour(
    lppathnamestr: PCWSTR
) -> *mut c_void {
    let path =
        paths::pcwstr_to_path(
            lppathnamestr
        );

    let new_path =
        remap_path(
            &path
        )
        .unwrap_or_else(
            || path.to_path_buf()
        );

    let wide_path =
        paths::path_to_widestring(
            &new_path
        );

    AddDllDirectory_Detour.call(
        wide_path.as_ptr()
    )
}

unsafe extern "system" fn getcommandlinew_detour()
    -> PCWSTR
{
    SANITIZED_COMMAND_LINE.as_ptr()
}

unsafe extern "system" fn getmodulefilenamew_detour(
    h_module: HMODULE,
    lp_filename: PWSTR,
    n_size: u32,
) -> u32 {
    let result =
        GetModuleFileNameW_Detour.call(
            h_module,
            lp_filename,
            n_size
        );

    if result == 0
        || n_size == 0
    {
        return result;
    }

    let written =
        result as usize;

    let buf_slice =
        slice::from_raw_parts(
            lp_filename,
            written
        );

    let actual_str =
        match WideString::from_vec(
            buf_slice.to_vec()
        )
        .to_string() {
            Ok(s) => s,
            Err(_) => return result,
        };

    let actual_path =
        NormalizedPath::new(
            &actual_str
        );

    let Some(source_path) =
        reverse_remap(
            &actual_path
        )
    else {
        return result;
    };

    let source_str =
        source_path.to_string_lossy();

    let Ok(source_wide) =
        U16CString::from_str(
            source_str.as_ref()
        )
    else {
        return result;
    };

    let with_nul =
        source_wide.as_slice_with_nul();

    if with_nul.len() as u32 <= n_size {
        ptr::copy_nonoverlapping(
            with_nul.as_ptr(),
            lp_filename,
            with_nul.len()
        );

        (with_nul.len() - 1) as u32
    } else {
        ptr::copy_nonoverlapping(
            with_nul.as_ptr(),
            lp_filename,
            n_size as usize
        );

        SetLastError(
            windows_sys::Win32::Foundation::
                ERROR_INSUFFICIENT_BUFFER
        );

        n_size
    }
}

/// Tokenize a Windows command line and emit a copy with the
/// shimloader's own switches and following values removed.
fn sanitize_command_line(
    input: &[u16]
) -> Vec<u16> {
    let tokens =
        tokenize_command_line(
            input
        );

    let mut keep =
        vec![true; tokens.len()];

    let mut skip_next =
        false;

    for (i, tok) in
        tokens.iter().enumerate()
    {
        if skip_next {
            keep[i] = false;
            skip_next = false;
            continue;
        }

        let unquoted =
            unquote_token(
                &input[
                    tok.start..tok.end
                ]
            );

        if SHIMLOADER_SWITCHES
            .iter()
            .any(
                |s| {
                    unquoted.eq_ignore_ascii_case(
                        s
                    )
                }
            )
        {
            keep[i] = false;
            skip_next = true;
        }
    }

    let mut out =
        Vec::<u16>::with_capacity(
            input.len()
        );

    let mut first_kept =
        true;

    for (i, tok) in
        tokens.iter().enumerate()
    {
        if !keep[i] {
            continue;
        }

        if first_kept {
            out.extend_from_slice(
                &input[
                    ..tok.end
                ]
            );

            first_kept = false;
        } else {
            out.push(
                b' ' as u16
            );

            out.extend_from_slice(
                &input[
                    tok.start..tok.end
                ]
            );
        }
    }

    out
}

struct CmdToken {
    start: usize,
    end: usize,
}

fn tokenize_command_line(
    input: &[u16]
) -> Vec<CmdToken> {
    const SPACE: u16 =
        b' ' as u16;

    const TAB: u16 =
        b'\t' as u16;

    const QUOTE: u16 =
        b'"' as u16;

    let mut tokens =
        Vec::new();

    let mut i =
        0;

    while i < input.len() {
        while i < input.len()
            && (
                input[i] == SPACE
                    || input[i] == TAB
            )
        {
            i += 1;
        }

        if i >= input.len() {
            break;
        }

        let start =
            i;

        let mut in_quote =
            false;

        while i < input.len() {
            let c =
                input[i];

            if c == QUOTE {
                in_quote =
                    !in_quote;
            } else if !in_quote
                && (
                    c == SPACE
                        || c == TAB
                )
            {
                break;
            }

            i += 1;
        }

        tokens.push(
            CmdToken {
                start,
                end: i
            }
        );
    }

    tokens
}

fn unquote_token(
    token: &[u16]
) -> String {
    const QUOTE: u16 =
        b'"' as u16;

    let trimmed =
        if token.len() >= 2
            && token[0] == QUOTE
            && token[token.len() - 1] == QUOTE
        {
            &token[
                1..token.len() - 1
            ]
        } else {
            token
        };

    String::from_utf16_lossy(
        trimmed
    )
}