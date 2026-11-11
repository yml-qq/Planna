use rusqlite::{Connection, OptionalExtension, Transaction, params};
use serde::{Deserialize, Serialize};

#[derive(Debug)]
pub enum StoreError {
    Sqlite(rusqlite::Error),
    Validation(&'static str),
    Conflict { server_version: i64 },
}

impl std::fmt::Display for StoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StoreError::Sqlite(err) => write!(f, "sqlite: {err}"),
            StoreError::Validation(message) => write!(f, "validation: {message}"),
            StoreError::Conflict { server_version } => {
                write!(f, "conflict: server version {server_version}")
            }
        }
    }
}

impl std::error::Error for StoreError {}

impl From<rusqlite::Error> for StoreError {
    fn from(value: rusqlite::Error) -> Self {
        StoreError::Sqlite(value)
    }
}

pub type StoreResult<T> = Result<T, StoreError>;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NewTask {
    pub id: String,
    pub list_id: String,
    pub title: String,
    pub time_zone: String,
    pub all_day: bool,
    pub due_at: Option<String>,
    pub due_local_date: Option<String>,
}

impl NewTask {
    fn validate(&self) -> StoreResult<()> {
        if self.id.is_empty() || self.list_id.is_empty() {
            return Err(StoreError::Validation("id and list_id are required"));
        }
        if self.title.trim().is_empty() || self.title.chars().count() > 500 {
            return Err(StoreError::Validation("title must be 1..=500 characters"));
        }
        if self.time_zone.is_empty() {
            return Err(StoreError::Validation("time_zone is required"));
        }
        match (self.all_day, &self.due_at, &self.due_local_date) {
            (true, Some(_), _) => Err(StoreError::Validation("all-day tasks cannot have due_at")),
            (true, None, None) => Err(StoreError::Validation("all-day tasks need due_local_date")),
            (false, _, Some(_)) => Err(StoreError::Validation(
                "timed tasks cannot have due_local_date",
            )),
            _ => Ok(()),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskPatch {
    pub title: Option<String>,
    pub status: Option<String>,
    pub priority: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoredTask {
    pub id: String,
    pub list_id: String,
    pub title: String,
    #[serde(default)]
    pub description: String,
    pub status: String,
    #[serde(default)]
    pub priority: i64,
    #[serde(default)]
    pub all_day: bool,
    #[serde(default)]
    pub time_zone: String,
    pub due_at: Option<String>,
    pub due_local_date: Option<String>,
    pub version: i64,
    #[serde(default)]
    pub updated_at: String,
    pub deleted_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OutboxEntry {
    pub mutation_id: String,
    pub entity_type: String,
    pub entity_id: String,
    pub operation: String,
    pub payload: String,
    pub request_hash: String,
    pub base_version: i64,
    pub created_at: String,
    pub attempts: i64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Change {
    pub entity_type: String,
    pub entity_id: String,
    pub operation: String,
    pub entity_version: i64,
    pub payload: String,
}

#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct PullOutcome {
    pub applied: usize,
    pub skipped_stale: usize,
}

pub struct Store {
    connection: Connection,
}

impl Store {
    pub fn open_in_memory() -> StoreResult<Self> {
        Self::from_connection(Connection::open_in_memory()?)
    }

    pub fn open(path: &std::path::Path) -> StoreResult<Self> {
        Self::from_connection(Connection::open(path)?)
    }

    fn from_connection(connection: Connection) -> StoreResult<Self> {
        connection.execute_batch("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;")?;
        let store = Store { connection };
        store.migrate()?;
        Ok(store)
    }

    fn migrate(&self) -> StoreResult<()> {
        self.connection.execute_batch(
            r#"
            CREATE TABLE IF NOT EXISTS local_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_lists (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version >= 1),
                deleted_at TEXT
            );
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                list_id TEXT NOT NULL REFERENCES task_lists(id),
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
                description TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL CHECK (status IN ('todo','in_progress','done','archived')),
                priority INTEGER NOT NULL CHECK (priority BETWEEN 0 AND 3),
                all_day INTEGER NOT NULL,
                time_zone TEXT NOT NULL,
                due_at TEXT,
                due_local_date TEXT,
                version INTEGER NOT NULL CHECK (version >= 1),
                updated_at TEXT NOT NULL,
                deleted_at TEXT
            );
            CREATE TABLE IF NOT EXISTS outbox (
                mutation_id TEXT PRIMARY KEY,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                operation TEXT NOT NULL CHECK (operation IN ('upsert','delete')),
                payload TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                base_version INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS sync_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            "#,
        )?;
        let has_base_version: i64 = self.connection.query_row(
            "SELECT count(*) FROM pragma_table_info('outbox') WHERE name = 'base_version'",
            [],
            |row| row.get(0),
        )?;
        if has_base_version == 0 {
            self.connection.execute_batch(
                "ALTER TABLE outbox ADD COLUMN base_version INTEGER NOT NULL DEFAULT 0;",
            )?;
        }
        Ok(())
    }

    /// Adds a list row (local-only helper used by tests and the inbox bootstrap).
    pub fn upsert_list(&self, id: &str, name: &str, kind: &str, version: i64) -> StoreResult<()> {
        self.connection.execute(
            "INSERT INTO task_lists(id, name, kind, version) VALUES (?1, ?2, ?3, ?4)
             ON CONFLICT(id) DO UPDATE SET name = excluded.name, kind = excluded.kind,
                version = excluded.version, deleted_at = NULL",
            params![id, name, kind, version],
        )?;
        Ok(())
    }

    /// Creates a task and its outbox row in one transaction. Replaying the same mutation id
    /// is idempotent and does not create a second row.
    pub fn create_task(
        &mut self,
        task: &NewTask,
        mutation_id: &str,
        now: &str,
    ) -> StoreResult<bool> {
        task.validate()?;
        if mutation_id.is_empty() || now.is_empty() {
            return Err(StoreError::Validation("mutation_id and now are required"));
        }
        let existing: Option<String> = self
            .connection
            .query_row(
                "SELECT mutation_id FROM outbox WHERE mutation_id = ?1",
                params![mutation_id],
                |row| row.get(0),
            )
            .optional()?;
        if existing.is_some() {
            return Ok(false);
        }
        let transaction = self.connection.transaction()?;
        insert_task(&transaction, task, now)?;
        let payload = serde_json::json!({
            "id": task.id,
            "list_id": task.list_id,
            "title": task.title,
            "time_zone": task.time_zone,
            "all_day": task.all_day,
            "due_at": task.due_at,
            "due_local_date": task.due_local_date,
            "status": "todo",
            "priority": 0,
            "version": 1
        })
        .to_string();
        insert_outbox(
            &transaction,
            OutboxWrite {
                mutation_id,
                entity_type: "task",
                entity_id: &task.id,
                operation: "upsert",
                base_version: 0,
                payload: &payload,
                now,
            },
        )?;
        transaction.commit()?;
        Ok(true)
    }

    /// Applies a local edit and its outbox row in one transaction; the expected version guards
    /// against concurrent local edits.
    pub fn update_task(
        &mut self,
        task_id: &str,
        patch: &TaskPatch,
        expected_version: i64,
        mutation_id: &str,
        now: &str,
    ) -> StoreResult<bool> {
        if mutation_id.is_empty() || now.is_empty() {
            return Err(StoreError::Validation("mutation_id and now are required"));
        }
        let existing: Option<String> = self
            .connection
            .query_row(
                "SELECT mutation_id FROM outbox WHERE mutation_id = ?1",
                params![mutation_id],
                |row| row.get(0),
            )
            .optional()?;
        if existing.is_some() {
            return Ok(false);
        }
        let transaction = self.connection.transaction()?;
        let (current_version, deleted): (i64, Option<String>) = transaction
            .query_row(
                "SELECT version, deleted_at FROM tasks WHERE id = ?1",
                params![task_id],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?
            .ok_or(StoreError::Validation("task not found"))?;
        if deleted.is_some() {
            return Err(StoreError::Validation("task is deleted"));
        }
        if current_version != expected_version {
            return Err(StoreError::Conflict {
                server_version: current_version,
            });
        }
        if let Some(title) = &patch.title
            && (title.trim().is_empty() || title.chars().count() > 500)
        {
            return Err(StoreError::Validation("title must be 1..=500 characters"));
        }
        if let Some(status) = &patch.status
            && !matches!(
                status.as_str(),
                "todo" | "in_progress" | "done" | "archived"
            )
        {
            return Err(StoreError::Validation("unknown status"));
        }
        if let Some(priority) = patch.priority
            && !(0..=3).contains(&priority)
        {
            return Err(StoreError::Validation("priority must be 0..=3"));
        }
        let next_version = current_version + 1;
        transaction.execute(
            "UPDATE tasks SET title = COALESCE(?2, title), status = COALESCE(?3, status),
                priority = COALESCE(?4, priority), version = ?5, updated_at = ?6
             WHERE id = ?1",
            params![
                task_id,
                patch.title,
                patch.status,
                patch.priority,
                next_version,
                now
            ],
        )?;
        let stored =
            load_task(&transaction, task_id)?.ok_or(StoreError::Validation("task disappeared"))?;
        let payload =
            serde_json::to_string(&stored).map_err(|_| StoreError::Validation("payload"))?;
        insert_outbox(
            &transaction,
            OutboxWrite {
                mutation_id,
                entity_type: "task",
                entity_id: task_id,
                operation: "upsert",
                base_version: expected_version,
                payload: &payload,
                now,
            },
        )?;
        transaction.commit()?;
        Ok(true)
    }

    pub fn tasks_for_list(&self, list_id: &str) -> StoreResult<Vec<StoredTask>> {
        let mut statement = self.connection.prepare(
            "SELECT id, list_id, title, description, status, priority, all_day, time_zone,
                due_at, due_local_date, version, updated_at, deleted_at
             FROM tasks WHERE list_id = ?1 AND deleted_at IS NULL ORDER BY priority DESC, id",
        )?;
        let rows = statement.query_map(params![list_id], map_task)?;
        let mut tasks = Vec::new();
        for row in rows {
            tasks.push(row?);
        }
        Ok(tasks)
    }

    pub fn pending_mutations(&self, limit: i64) -> StoreResult<Vec<OutboxEntry>> {
        let mut statement = self.connection.prepare(
            "SELECT mutation_id, entity_type, entity_id, operation, payload, request_hash,
                base_version, created_at, attempts
             FROM outbox ORDER BY created_at, mutation_id LIMIT ?1",
        )?;
        let rows = statement.query_map(params![limit], |row| {
            Ok(OutboxEntry {
                mutation_id: row.get(0)?,
                entity_type: row.get(1)?,
                entity_id: row.get(2)?,
                operation: row.get(3)?,
                payload: row.get(4)?,
                request_hash: row.get(5)?,
                base_version: row.get(6)?,
                created_at: row.get(7)?,
                attempts: row.get(8)?,
            })
        })?;
        let mut entries = Vec::new();
        for row in rows {
            entries.push(row?);
        }
        Ok(entries)
    }

    pub fn remove_mutation(&self, mutation_id: &str) -> StoreResult<()> {
        self.connection.execute(
            "DELETE FROM outbox WHERE mutation_id = ?1",
            params![mutation_id],
        )?;
        Ok(())
    }

    pub fn record_failure(&self, mutation_id: &str, error: &str) -> StoreResult<()> {
        self.connection.execute(
            "UPDATE outbox SET attempts = attempts + 1, last_error = ?2 WHERE mutation_id = ?1",
            params![mutation_id, error],
        )?;
        Ok(())
    }

    /// Applies pull changes with per-entity version monotonicity and tombstones; the caller has
    /// already mapped server payloads to `Change` values.
    pub fn apply_pull(&mut self, changes: &[Change]) -> StoreResult<PullOutcome> {
        let mut outcome = PullOutcome::default();
        let transaction = self.connection.transaction()?;
        for change in changes {
            if change.entity_type != "task" {
                outcome.skipped_stale += 1;
                continue;
            }
            let local: Option<(i64,)> = transaction
                .query_row(
                    "SELECT version FROM tasks WHERE id = ?1",
                    params![change.entity_id],
                    |row| Ok((row.get(0)?,)),
                )
                .optional()?;
            if let Some((version,)) = local
                && version >= change.entity_version
            {
                outcome.skipped_stale += 1;
                continue;
            }
            let payload: serde_json::Value = serde_json::from_str(&change.payload)
                .map_err(|_| StoreError::Validation("pull payload is not JSON"))?;
            if change.operation == "delete" {
                let updated_at = payload
                    .get("updated_at")
                    .and_then(|value| value.as_str())
                    .unwrap_or_default();
                transaction.execute(
                    "UPDATE tasks SET deleted_at = COALESCE(?2, deleted_at), version = ?3 WHERE id = ?1",
                    params![change.entity_id, updated_at, change.entity_version],
                )?;
                outcome.applied += 1;
                continue;
            }
            let stored: StoredTask = serde_json::from_value(payload)
                .map_err(|_| StoreError::Validation("pull payload is not a task"))?;
            transaction.execute(
                "INSERT INTO task_lists(id, name, kind, version) VALUES (?1, 'Inbox', 'inbox', 1)
                     ON CONFLICT(id) DO NOTHING",
                params![stored.list_id],
            )?;
            transaction.execute(
                "INSERT INTO tasks(id, list_id, title, description, status, priority, all_day,
                        time_zone, due_at, due_local_date, version, updated_at, deleted_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)
                     ON CONFLICT(id) DO UPDATE SET list_id = excluded.list_id,
                        title = excluded.title, description = excluded.description,
                        status = excluded.status, priority = excluded.priority,
                        all_day = excluded.all_day, time_zone = excluded.time_zone,
                        due_at = excluded.due_at, due_local_date = excluded.due_local_date,
                        version = excluded.version, updated_at = excluded.updated_at,
                        deleted_at = excluded.deleted_at",
                params![
                    stored.id,
                    stored.list_id,
                    stored.title,
                    stored.description,
                    stored.status,
                    stored.priority,
                    stored.all_day,
                    stored.time_zone,
                    stored.due_at,
                    stored.due_local_date,
                    change.entity_version,
                    stored.updated_at,
                    stored.deleted_at
                ],
            )?;
            outcome.applied += 1;
        }
        transaction.commit()?;
        Ok(outcome)
    }

    pub fn set_cursor_value(&self, value: &str) -> StoreResult<()> {
        self.connection.execute(
            "INSERT INTO sync_state(key, value) VALUES ('pull_cursor', ?1)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![value],
        )?;
        Ok(())
    }

    pub fn cursor_value(&self) -> StoreResult<Option<String>> {
        self.connection
            .query_row(
                "SELECT value FROM sync_state WHERE key = 'pull_cursor'",
                [],
                |row| row.get(0),
            )
            .optional()
            .map_err(StoreError::from)
    }
}

fn insert_task(transaction: &Transaction<'_>, task: &NewTask, now: &str) -> StoreResult<()> {
    transaction.execute(
        "INSERT INTO tasks(id, list_id, title, time_zone, all_day, due_at, due_local_date,
            version, updated_at, status, priority, description)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 1, ?8, 'todo', 0, '')
         ON CONFLICT(id) DO UPDATE SET title = excluded.title, updated_at = excluded.updated_at,
            version = tasks.version + 1",
        params![
            task.id,
            task.list_id,
            task.title.trim(),
            task.time_zone,
            task.all_day,
            task.due_at,
            task.due_local_date,
            now
        ],
    )?;
    Ok(())
}

struct OutboxWrite<'a> {
    mutation_id: &'a str,
    entity_type: &'a str,
    entity_id: &'a str,
    operation: &'a str,
    base_version: i64,
    payload: &'a str,
    now: &'a str,
}

fn insert_outbox(transaction: &Transaction<'_>, entry: OutboxWrite<'_>) -> StoreResult<()> {
    transaction.execute(
        "INSERT INTO outbox(mutation_id, entity_type, entity_id, operation, payload, request_hash,
            base_version, created_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        params![
            entry.mutation_id,
            entry.entity_type,
            entry.entity_id,
            entry.operation,
            entry.payload,
            format!(
                "{}:{}:{}",
                entry.operation, entry.entity_id, entry.mutation_id
            ),
            entry.base_version,
            entry.now
        ],
    )?;
    Ok(())
}

fn load_task(transaction: &Transaction<'_>, task_id: &str) -> StoreResult<Option<StoredTask>> {
    transaction
        .query_row(
            "SELECT id, list_id, title, description, status, priority, all_day, time_zone,
                due_at, due_local_date, version, updated_at, deleted_at
             FROM tasks WHERE id = ?1",
            params![task_id],
            map_task,
        )
        .optional()
        .map_err(StoreError::from)
}

fn map_task(row: &rusqlite::Row<'_>) -> rusqlite::Result<StoredTask> {
    Ok(StoredTask {
        id: row.get(0)?,
        list_id: row.get(1)?,
        title: row.get(2)?,
        description: row.get(3)?,
        status: row.get(4)?,
        priority: row.get(5)?,
        all_day: row.get(6)?,
        time_zone: row.get(7)?,
        due_at: row.get(8)?,
        due_local_date: row.get(9)?,
        version: row.get(10)?,
        updated_at: row.get(11)?,
        deleted_at: row.get(12)?,
    })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TaskPayload {
    pub id: String,
    pub list_id: String,
    pub title: String,
    #[serde(default)]
    pub description: String,
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub priority: i64,
    #[serde(default)]
    pub all_day: bool,
    #[serde(default)]
    pub time_zone: String,
    pub due_at: Option<String>,
    pub due_local_date: Option<String>,
    #[serde(default = "default_version")]
    pub version: i64,
    #[serde(default)]
    pub updated_at: String,
    pub deleted_at: Option<String>,
}

fn default_status() -> String {
    "todo".to_string()
}

fn default_version() -> i64 {
    1
}
