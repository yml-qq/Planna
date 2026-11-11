use std::sync::Mutex;

use planna_desktop_core::{
    NewTask, OutboxEntry, PullResponse, PushRequest, PushResponse, Store, StoredTask, SyncOutcome,
    SyncTransport, sync_once,
};
use serde::Serialize;
use tauri::{Manager, State};

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
}

impl SyncTransport for HttpTransport {
    fn push(&mut self, request: &PushRequest) -> Result<PushResponse, String> {
        let response = self
            .client
            .post(format!("{}/sync/push", self.base_url))
            .bearer_auth(&self.access_token)
            .json(request)
            .send()
            .map_err(|error| error.to_string())?;
        if !response.status().is_success() {
            return Err(format!("HTTP {}", response.status().as_u16()));
        }
        response
            .json::<PushResponse>()
            .map_err(|error| error.to_string())
    }

    fn pull(&mut self, cursor: Option<&str>, limit: i64) -> Result<PullResponse, String> {
        let mut request = self
            .client
            .get(format!("{}/sync/pull", self.base_url))
            .bearer_auth(&self.access_token)
            .query(&[("limit", limit.to_string())]);
        if let Some(cursor) = cursor {
            request = request.query(&[("cursor", cursor.to_string())]);
        }
        let response = request.send().map_err(|error| error.to_string())?;
        if !response.status().is_success() {
            return Err(format!("HTTP {}", response.status().as_u16()));
        }
        response
            .json::<PullResponse>()
            .map_err(|error| error.to_string())
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
    sync_once(&mut store, &mut transport, &device_id, 10)
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
            run_sync
        ])
        .run(tauri::generate_context!())
        .expect("error while running Planna desktop");
}
