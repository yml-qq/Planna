use std::sync::Mutex;

use planna_desktop_core::{
    HabitPatchLocal, NewHabit, NewTask, OutboxEntry, PullResponse, PushRequest, PushResponse,
    Store, StoredHabit, StoredHabitLog, StoredTask, SyncOutcome, SyncTransport, TransportError,
    sync_with_retry,
};
use serde::{Deserialize, Serialize};
use tauri::{Manager, State};

const KEYRING_SERVICE: &str = "app.planna.desktop";
const KEYRING_USER: &str = "refresh-token";

struct AppState {
    store: Mutex<Store>,
}

#[derive(Serialize)]
struct TaskDto {
    id: String,
    list_id: String,
    title: String,
    status: String,
    version: i64,
}

impl From<StoredTask> for TaskDto {
    fn from(task: StoredTask) -> Self {
        TaskDto {
            id: task.id,
            list_id: task.list_id,
            title: task.title,
            status: task.status,
            version: task.version,
        }
    }
}

#[derive(Serialize)]
struct OutboxDto {
    mutation_id: String,
    entity_type: String,
    entity_id: String,
    operation: String,
    payload: String,
    attempts: i64,
}

impl From<OutboxEntry> for OutboxDto {
    fn from(entry: OutboxEntry) -> Self {
        OutboxDto {
            mutation_id: entry.mutation_id,
            entity_type: entry.entity_type,
            entity_id: entry.entity_id,
            operation: entry.operation,
            payload: entry.payload,
            attempts: entry.attempts,
        }
    }
}

#[derive(Serialize)]
struct HabitDto {
    id: String,
    title: String,
    schedule_type: String,
    weekdays: Option<Vec<i16>>,
    target_quantity: i64,
    unit: String,
    time_zone: String,
    paused_at: Option<String>,
    version: i64,
    updated_at: String,
}

impl From<StoredHabit> for HabitDto {
    fn from(habit: StoredHabit) -> Self {
        HabitDto {
            id: habit.id,
            title: habit.title,
            schedule_type: habit.schedule_type,
            weekdays: habit.weekdays,
            target_quantity: habit.target_quantity,
            unit: habit.unit,
            time_zone: habit.time_zone,
            paused_at: habit.paused_at,
            version: habit.version,
            updated_at: habit.updated_at,
        }
    }
}

#[derive(Serialize)]
struct HabitLogDto {
    id: String,
    habit_id: String,
    log_date: String,
    quantity: i64,
    status: String,
    version: i64,
    updated_at: String,
}

impl From<StoredHabitLog> for HabitLogDto {
    fn from(log: StoredHabitLog) -> Self {
        HabitLogDto {
            id: log.id,
            habit_id: log.habit_id,
            log_date: log.log_date,
            quantity: log.quantity,
            status: log.status,
            version: log.version,
            updated_at: log.updated_at,
        }
    }
}

#[tauri::command]
fn list_habits(state: State<'_, AppState>) -> Result<Vec<HabitDto>, String> {
    let store = state.store.lock().map_err(|_| "store lock poisoned")?;
    store
        .habits()
        .map(|habits| habits.into_iter().map(HabitDto::from).collect())
        .map_err(|error| error.to_string())
}

#[tauri::command]
#[allow(clippy::too_many_arguments)]
fn create_habit(
    state: State<'_, AppState>,
    id: String,
    title: String,
    schedule_type: String,
    weekdays: Option<Vec<i16>>,
    target_quantity: i64,
    unit: String,
    time_zone: String,
) -> Result<HabitDto, String> {
    let mut store = state.store.lock().map_err(|_| "store lock poisoned")?;
    let habit = NewHabit {
        id: id.clone(),
        title,
        schedule_type,
        weekdays,
        target_quantity,
        unit,
        time_zone,
    };
    let mutation_id = uuid::Uuid::new_v4().to_string();
    store
        .create_habit(&habit, &mutation_id, &now())
        .map_err(|error| error.to_string())?;
    store
        .habits()
        .map_err(|error| error.to_string())?
        .into_iter()
        .find(|stored| stored.id == id)
        .map(HabitDto::from)
        .ok_or_else(|| "HABIT_NOT_FOUND_AFTER_CREATE".to_string())
}

#[tauri::command]
fn update_habit(
    state: State<'_, AppState>,
    habit_id: String,
    title: Option<String>,
    target_quantity: Option<i64>,
    unit: Option<String>,
    paused: Option<bool>,
) -> Result<HabitDto, String> {
    let mut store = state.store.lock().map_err(|_| "store lock poisoned")?;
    let patch = HabitPatchLocal {
        title,
        target_quantity,
        unit,
        paused,
    };
    let mutation_id = uuid::Uuid::new_v4().to_string();
    store
        .update_habit(&habit_id, &patch, &mutation_id, &now())
        .map_err(|error| error.to_string())?;
    store
        .habits()
        .map_err(|error| error.to_string())?
        .into_iter()
        .find(|habit| habit.id == habit_id)
        .map(HabitDto::from)
        .ok_or_else(|| "HABIT_NOT_FOUND_AFTER_UPDATE".to_string())
}

#[tauri::command]
fn upsert_habit_log(
    state: State<'_, AppState>,
    habit_id: String,
    log_date: String,
    quantity: i64,
    status: String,
) -> Result<HabitLogDto, String> {
    let mut store = state.store.lock().map_err(|_| "store lock poisoned")?;
    let mutation_id = uuid::Uuid::new_v4().to_string();
    store
        .upsert_habit_log(
            &habit_id,
            &log_date,
            quantity,
            &status,
            &mutation_id,
            &now(),
        )
        .map_err(|error| error.to_string())?;
    store
        .habit_logs(&habit_id, &log_date, &log_date)
        .map_err(|error| error.to_string())?
        .into_iter()
        .next()
        .map(HabitLogDto::from)
        .ok_or_else(|| "HABIT_LOG_NOT_FOUND_AFTER_UPSERT".to_string())
}

#[tauri::command]
fn list_habit_logs(
    state: State<'_, AppState>,
    habit_id: String,
    from: String,
    to: String,
) -> Result<Vec<HabitLogDto>, String> {
    let store = state.store.lock().map_err(|_| "store lock poisoned")?;
    store
        .habit_logs(&habit_id, &from, &to)
        .map(|logs| logs.into_iter().map(HabitLogDto::from).collect())
        .map_err(|error| error.to_string())
}

#[tauri::command]
fn list_tasks(state: State<'_, AppState>, list_id: String) -> Result<Vec<TaskDto>, String> {
    let store = state.store.lock().map_err(|_| "store lock poisoned")?;
    store
        .tasks_for_list(&list_id)
        .map(|tasks| tasks.into_iter().map(TaskDto::from).collect())
        .map_err(|error| error.to_string())
}

#[tauri::command]
fn create_task(
    state: State<'_, AppState>,
    id: String,
    list_id: String,
    title: String,
) -> Result<TaskDto, String> {
    let mut store = state.store.lock().map_err(|_| "store lock poisoned")?;
    let task = NewTask {
        id: id.clone(),
        list_id: list_id.clone(),
        title,
        time_zone: "Asia/Shanghai".to_string(),
        all_day: false,
        due_at: None,
        due_local_date: None,
    };
    let mutation_id = uuid::Uuid::new_v4().to_string();
    let created = store
        .create_task(&task, &mutation_id, &now())
        .map_err(|error| error.to_string())?;
    if !created {
        return Err("DUPLICATE_MUTATION".to_string());
    }
    store
        .tasks_for_list(&list_id)
        .map_err(|error| error.to_string())?
        .into_iter()
        .find(|stored| stored.id == id)
        .map(TaskDto::from)
        .ok_or_else(|| "TASK_NOT_FOUND_AFTER_CREATE".to_string())
}

#[tauri::command]
fn pending_mutations(state: State<'_, AppState>, limit: i64) -> Result<Vec<OutboxDto>, String> {
    let store = state.store.lock().map_err(|_| "store lock poisoned")?;
    store
        .pending_mutations(limit)
        .map(|entries| entries.into_iter().map(OutboxDto::from).collect())
        .map_err(|error| error.to_string())
}

fn now() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or(0);
    format!("{millis}")
}

struct HttpTransport {
    base_url: String,
    access_token: String,
    client: reqwest::blocking::Client,
}

impl HttpTransport {
    fn new(base_url: String, access_token: String) -> Self {
        Self {
            base_url: base_url.trim_end_matches('/').to_string(),
            access_token,
            client: reqwest::blocking::Client::new(),
        }
    }

    fn classify(status: reqwest::StatusCode) -> TransportError {
        if status == reqwest::StatusCode::GONE {
            return TransportError::CursorExpired;
        }
        if status == reqwest::StatusCode::TOO_MANY_REQUESTS || status.is_server_error() {
            return TransportError::Retryable(format!("HTTP {}", status.as_u16()));
        }
        TransportError::Fatal(format!("HTTP {}", status.as_u16()))
    }
}

impl SyncTransport for HttpTransport {
    fn push(&mut self, request: &PushRequest) -> Result<PushResponse, TransportError> {
        let response = self
            .client
            .post(format!("{}/sync/push", self.base_url))
            .bearer_auth(&self.access_token)
            .json(request)
            .send()
            .map_err(|error| TransportError::Retryable(error.to_string()))?;
        if !response.status().is_success() {
            return Err(Self::classify(response.status()));
        }
        response
            .json::<PushResponse>()
            .map_err(|error| TransportError::Fatal(error.to_string()))
    }

    fn pull(&mut self, cursor: Option<&str>, limit: i64) -> Result<PullResponse, TransportError> {
        let mut request = self
            .client
            .get(format!("{}/sync/pull", self.base_url))
            .bearer_auth(&self.access_token)
            .query(&[("limit", limit.to_string())]);
        if let Some(cursor) = cursor {
            request = request.query(&[("cursor", cursor.to_string())]);
        }
        let response = request
            .send()
            .map_err(|error| TransportError::Retryable(error.to_string()))?;
        if !response.status().is_success() {
            return Err(Self::classify(response.status()));
        }
        response
            .json::<PullResponse>()
            .map_err(|error| TransportError::Fatal(error.to_string()))
    }
}

#[tauri::command]
fn run_sync(
    state: State<'_, AppState>,
    base_url: String,
    access_token: String,
    device_id: String,
) -> Result<SyncOutcome, String> {
    let mut store = state.store.lock().map_err(|_| "store lock poisoned")?;
    let mut transport = HttpTransport::new(base_url, access_token);
    sync_with_retry(&mut store, &mut transport, &device_id, 10, 3, |attempt| {
        std::thread::sleep(std::time::Duration::from_millis(
            500 * 2u64.pow(attempt.min(4)),
        ));
    })
    .map_err(|error| error.to_string())
}

#[derive(Serialize)]
struct SessionDto {
    access_token: String,
    expires_in: i32,
}

#[derive(Deserialize)]
struct AuthTokens {
    access_token: String,
    refresh_token: String,
    expires_in: i32,
}

fn http_client() -> reqwest::blocking::Client {
    reqwest::blocking::Client::new()
}

fn base(base_url: &str) -> String {
    base_url.trim_end_matches('/').to_string()
}

fn store_refresh_token(refresh_token: &str) -> Result<(), String> {
    let entry =
        keyring::Entry::new(KEYRING_SERVICE, KEYRING_USER).map_err(|error| error.to_string())?;
    entry
        .set_password(refresh_token)
        .map_err(|error| error.to_string())
}

fn read_refresh_token() -> Result<String, String> {
    let entry =
        keyring::Entry::new(KEYRING_SERVICE, KEYRING_USER).map_err(|error| error.to_string())?;
    entry.get_password().map_err(|_| "NO_SESSION".to_string())
}

fn clear_refresh_token() {
    if let Ok(entry) = keyring::Entry::new(KEYRING_SERVICE, KEYRING_USER) {
        let _ = entry.delete_credential();
    }
}

#[tauri::command]
fn request_login_code(
    base_url: String,
    email: String,
    device_id: String,
) -> Result<String, String> {
    let response = http_client()
        .post(format!("{}/auth/login/challenges", base(&base_url)))
        .header("Idempotency-Key", uuid::Uuid::new_v4().to_string())
        .header("X-Device-Id", device_id)
        .json(&serde_json::json!({ "email": email }))
        .send()
        .map_err(|error| error.to_string())?;
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status().as_u16()));
    }
    let body: serde_json::Value = response.json().map_err(|error| error.to_string())?;
    Ok(body
        .get("expires_at")
        .and_then(|value| value.as_str())
        .unwrap_or_default()
        .to_string())
}

#[tauri::command]
fn login_with_code(
    base_url: String,
    email: String,
    code: String,
    device_name: String,
    device_id: String,
) -> Result<SessionDto, String> {
    let response = http_client()
        .post(format!("{}/auth/login", base(&base_url)))
        .header("X-Device-Id", device_id)
        .json(&serde_json::json!({
            "email": email,
            "code": code,
            "device_name": device_name,
            "platform": "windows"
        }))
        .send()
        .map_err(|error| error.to_string())?;
    if response.status() == reqwest::StatusCode::UNAUTHORIZED {
        return Err("INVALID_CODE".to_string());
    }
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status().as_u16()));
    }
    let tokens: AuthTokens = response.json().map_err(|error| error.to_string())?;
    store_refresh_token(&tokens.refresh_token)?;
    Ok(SessionDto {
        access_token: tokens.access_token,
        expires_in: tokens.expires_in,
    })
}

#[tauri::command]
fn restore_session(base_url: String) -> Result<SessionDto, String> {
    let refresh_token = read_refresh_token()?;
    let response = http_client()
        .post(format!("{}/auth/refresh", base(&base_url)))
        .json(&serde_json::json!({ "refresh_token": refresh_token }))
        .send()
        .map_err(|error| error.to_string())?;
    if response.status() == reqwest::StatusCode::UNAUTHORIZED {
        clear_refresh_token();
        return Err("SESSION_EXPIRED".to_string());
    }
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status().as_u16()));
    }
    let tokens: AuthTokens = response.json().map_err(|error| error.to_string())?;
    store_refresh_token(&tokens.refresh_token)?;
    Ok(SessionDto {
        access_token: tokens.access_token,
        expires_in: tokens.expires_in,
    })
}

#[derive(Deserialize)]
struct DeviceRow {
    id: String,
    version: i64,
    is_current: bool,
}

#[derive(Deserialize)]
struct DevicePage {
    #[serde(default)]
    devices: Vec<DeviceRow>,
}

#[tauri::command]
fn sign_out_server(base_url: String, access_token: String) -> Result<(), String> {
    let client = http_client();
    let page = client
        .get(format!("{}/auth/devices?limit=50", base(&base_url)))
        .bearer_auth(&access_token)
        .send()
        .map_err(|error| error.to_string())?;
    if !page.status().is_success() {
        return Err(format!("HTTP {}", page.status().as_u16()));
    }
    let page: DevicePage = page.json().map_err(|error| error.to_string())?;
    let Some(current) = page.devices.into_iter().find(|device| device.is_current) else {
        clear_refresh_token();
        return Ok(());
    };
    let response = client
        .post(format!(
            "{}/auth/devices/{}/revoke",
            base(&base_url),
            current.id
        ))
        .bearer_auth(&access_token)
        .header("Idempotency-Key", uuid::Uuid::new_v4().to_string())
        .header("If-Match", format!("\"v{}\"", current.version))
        .send()
        .map_err(|error| error.to_string())?;
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status().as_u16()));
    }
    clear_refresh_token();
    Ok(())
}

#[tauri::command]
fn sign_out_local() -> Result<(), String> {
    clear_refresh_token();
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let directory = app
                .path()
                .app_data_dir()
                .map_err(|error| format!("app data dir unavailable: {error}"))?;
            std::fs::create_dir_all(&directory)
                .map_err(|error| format!("cannot create app data dir: {error}"))?;
            let store = Store::open(&directory.join("planna.db"))
                .map_err(|error| format!("cannot open local store: {error}"))?;
            store
                .upsert_list("list-inbox", "收件箱", "inbox", 1)
                .map_err(|error| format!("cannot ensure inbox list: {error}"))?;
            app.manage(AppState {
                store: Mutex::new(store),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_tasks,
            create_task,
            pending_mutations,
            list_habits,
            create_habit,
            update_habit,
            upsert_habit_log,
            list_habit_logs,
            run_sync,
            request_login_code,
            login_with_code,
            restore_session,
            sign_out_server,
            sign_out_local
        ])
        .run(tauri::generate_context!())
        .expect("error while running Planna desktop");
}
