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

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct NewHabit {
    pub id: String,
    pub title: String,
    pub schedule_type: String,
    #[serde(default)]
    pub weekdays: Option<Vec<i16>>,
    pub target_quantity: i64,
    pub unit: String,
    pub time_zone: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct StoredHabit {
    pub id: String,
    pub title: String,
    pub schedule_type: String,
    #[serde(default)]
    pub weekdays: Option<Vec<i16>>,
    pub target_quantity: i64,
    pub unit: String,
    pub time_zone: String,
    #[serde(default)]
    pub paused_at: Option<String>,
    pub version: i64,
    #[serde(default)]
    pub updated_at: String,
    #[serde(default)]
    pub deleted_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct StoredHabitLog {
    pub id: String,
    pub habit_id: String,
    pub log_date: String,
    pub quantity: i64,
    pub status: String,
    pub version: i64,
    #[serde(default)]
    pub updated_at: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct HabitPatchLocal {
    pub title: Option<String>,
    pub schedule_type: Option<String>,
    pub weekdays: Option<Vec<i16>>,
    pub target_quantity: Option<i64>,
    pub unit: Option<String>,
    pub paused: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConflictEntry {
    pub mutation_id: String,
    pub entity_type: String,
    pub entity_id: String,
    pub server_entity: String,
    pub server_version: i64,
    pub created_at: String,
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
    pub last_error: Option<String>,
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
            CREATE TABLE IF NOT EXISTS habits (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
                schedule_type TEXT NOT NULL CHECK (schedule_type IN ('daily','weekly','weekdays')),
                weekdays TEXT,
                target_quantity INTEGER NOT NULL CHECK (target_quantity BETWEEN 1 AND 1000),
                unit TEXT NOT NULL DEFAULT 'count',
                time_zone TEXT NOT NULL,
                paused_at TEXT,
                version INTEGER NOT NULL CHECK (version >= 1),
                updated_at TEXT NOT NULL,
                deleted_at TEXT
            );
            CREATE TABLE IF NOT EXISTS habit_logs (
                id TEXT PRIMARY KEY,
                habit_id TEXT NOT NULL,
                log_date TEXT NOT NULL,
                quantity INTEGER NOT NULL CHECK (quantity BETWEEN 0 AND 1000),
                status TEXT NOT NULL CHECK (status IN ('done','skipped')),
                version INTEGER NOT NULL CHECK (version >= 1),
                updated_at TEXT NOT NULL,
                UNIQUE (habit_id, log_date)
            );
            CREATE TABLE IF NOT EXISTS sync_conflicts (
                mutation_id TEXT PRIMARY KEY,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                server_entity TEXT NOT NULL,
                server_version INTEGER NOT NULL,
                created_at TEXT NOT NULL
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
                base_version, created_at, attempts, last_error
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
                last_error: row.get(9)?,
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
            match change.entity_type.as_str() {
                "habit" => {
                    apply_habit_pull(&transaction, change, &mut outcome)?;
                    continue;
                }
                "habit_log" => {
                    apply_habit_log_pull(&transaction, change, &mut outcome)?;
                    continue;
                }
                "task" => {}
                _ => {
                    outcome.skipped_stale += 1;
                    continue;
                }
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

    /// Applies a local habit edit (title/target/unit/pause) with an outbox mutation in one
    /// transaction; the mutation carries the pre-edit version as base_version.
    pub fn update_habit(
        &mut self,
        habit_id: &str,
        patch: &HabitPatchLocal,
        mutation_id: &str,
        now: &str,
    ) -> StoreResult<bool> {
        if mutation_id.is_empty()
            || now.is_empty()
            || (patch.title.is_none()
                && patch.schedule_type.is_none()
                && patch.weekdays.is_none()
                && patch.target_quantity.is_none()
                && patch.unit.is_none()
                && patch.paused.is_none())
        {
            return Err(StoreError::Validation("empty habit patch"));
        }
        if let Some(title) = &patch.title
            && (title.trim().is_empty() || title.chars().count() > 200)
        {
            return Err(StoreError::Validation("invalid habit title"));
        }
        if let Some(target) = patch.target_quantity
            && !(1..=1000).contains(&target)
        {
            return Err(StoreError::Validation("invalid habit target"));
        }
        if let Some(unit) = &patch.unit
            && (unit.trim().is_empty() || unit.chars().count() > 20)
        {
            return Err(StoreError::Validation("invalid habit unit"));
        }
        if mutation_exists(&self.connection, mutation_id)? {
            return Ok(false);
        }
        let transaction = self.connection.transaction()?;
        let row = transaction
            .query_row(
                "SELECT id, title, schedule_type, weekdays, target_quantity, unit, time_zone,
                    paused_at, version, updated_at, deleted_at
                 FROM habits WHERE id = ?1",
                params![habit_id],
                map_habit,
            )
            .optional()?
            .ok_or(StoreError::Validation("habit not found"))?;
        if row.deleted_at.is_some() {
            return Err(StoreError::Validation("habit is deleted"));
        }
        let title = patch
            .title
            .as_ref()
            .map(|value| value.trim().to_string())
            .unwrap_or_else(|| row.title.clone());
        let schedule_type = patch
            .schedule_type
            .clone()
            .unwrap_or_else(|| row.schedule_type.clone());
        if !matches!(schedule_type.as_str(), "daily" | "weekly" | "weekdays") {
            return Err(StoreError::Validation("invalid habit schedule"));
        }
        let weekdays: Option<Vec<i16>> = if schedule_type == "weekly" {
            let days = patch
                .weekdays
                .clone()
                .or_else(|| row.weekdays.clone())
                .ok_or(StoreError::Validation("weekly habits need weekdays"))?;
            let mut sorted = days;
            sorted.sort_unstable();
            sorted.dedup();
            if sorted.is_empty()
                || sorted.len() > 7
                || sorted.iter().any(|day| !(1..=7).contains(day))
            {
                return Err(StoreError::Validation("invalid weekdays"));
            }
            Some(sorted)
        } else {
            None
        };
        let target = patch.target_quantity.unwrap_or(row.target_quantity);
        let unit = patch
            .unit
            .as_ref()
            .map(|value| value.trim().to_string())
            .unwrap_or_else(|| row.unit.clone());
        let paused_at = match patch.paused {
            Some(true) => Some(now.to_string()),
            Some(false) => None,
            None => row.paused_at.clone(),
        };
        let version = row.version + 1;
        let weekdays_json = weekdays
            .as_ref()
            .map(|days| serde_json::to_string(days).unwrap_or_else(|_| "[]".to_string()));
        transaction.execute(
            "UPDATE habits SET title = ?2, schedule_type = ?3, weekdays = ?4,
                target_quantity = ?5, unit = ?6, paused_at = ?7, version = ?8, updated_at = ?9
             WHERE id = ?1",
            params![
                habit_id,
                title,
                schedule_type,
                weekdays_json,
                target,
                unit,
                paused_at,
                version,
                now
            ],
        )?;
        let mut payload = serde_json::Map::new();
        if let Some(value) = &patch.title {
            payload.insert(
                "title".to_string(),
                serde_json::Value::String(value.trim().to_string()),
            );
        }
        if let Some(value) = &patch.schedule_type {
            payload.insert(
                "schedule_type".to_string(),
                serde_json::Value::String(value.clone()),
            );
        }
        if let Some(value) = &weekdays {
            payload.insert(
                "weekdays".to_string(),
                serde_json::Value::Array(
                    value
                        .iter()
                        .map(|day| serde_json::Value::from(*day))
                        .collect(),
                ),
            );
        }
        if let Some(value) = patch.target_quantity {
            payload.insert(
                "target_quantity".to_string(),
                serde_json::Value::from(value),
            );
        }
        if let Some(value) = &patch.unit {
            payload.insert(
                "unit".to_string(),
                serde_json::Value::String(value.trim().to_string()),
            );
        }
        if let Some(value) = patch.paused {
            payload.insert("paused".to_string(), serde_json::Value::Bool(value));
        }
        let payload = serde_json::Value::Object(payload).to_string();
        insert_outbox(
            &transaction,
            OutboxWrite {
                mutation_id,
                entity_type: "habit",
                entity_id: habit_id,
                operation: "upsert",
                base_version: row.version,
                payload: &payload,
                now,
            },
        )?;
        transaction.commit()?;
        Ok(true)
    }

    /// Creates a habit locally and queues its push mutation in the same transaction.
    pub fn create_habit(
        &mut self,
        habit: &NewHabit,
        mutation_id: &str,
        now: &str,
    ) -> StoreResult<bool> {
        if habit.id.is_empty() || habit.title.trim().is_empty() || habit.title.chars().count() > 200
        {
            return Err(StoreError::Validation("invalid habit"));
        }
        if !matches!(
            habit.schedule_type.as_str(),
            "daily" | "weekly" | "weekdays"
        ) || habit.target_quantity < 1
            || habit.target_quantity > 1000
            || habit.unit.trim().is_empty()
            || habit.unit.chars().count() > 20
            || habit.time_zone.is_empty()
        {
            return Err(StoreError::Validation("invalid habit"));
        }
        if habit.schedule_type == "weekly"
            && habit
                .weekdays
                .as_ref()
                .is_none_or(|days| days.is_empty() || days.len() > 7)
        {
            return Err(StoreError::Validation("weekly habits need weekdays"));
        }
        if mutation_id.is_empty() || now.is_empty() {
            return Err(StoreError::Validation("mutation_id and now are required"));
        }
        if mutation_exists(&self.connection, mutation_id)? {
            return Ok(false);
        }
        let transaction = self.connection.transaction()?;
        let weekdays = habit.weekdays.as_ref().map(|days| {
            let mut sorted = days.clone();
            sorted.sort_unstable();
            sorted.dedup();
            serde_json::to_string(&sorted).unwrap_or_else(|_| "[]".to_string())
        });
        transaction.execute(
            "INSERT INTO habits(id, title, schedule_type, weekdays, target_quantity, unit,
                time_zone, version, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 1, ?8)",
            params![
                habit.id,
                habit.title.trim(),
                habit.schedule_type,
                weekdays,
                habit.target_quantity,
                habit.unit.trim(),
                habit.time_zone,
                now
            ],
        )?;
        let payload =
            serde_json::to_string(habit).map_err(|_| StoreError::Validation("habit payload"))?;
        insert_outbox(
            &transaction,
            OutboxWrite {
                mutation_id,
                entity_type: "habit",
                entity_id: &habit.id,
                operation: "upsert",
                base_version: 0,
                payload: &payload,
                now,
            },
        )?;
        transaction.commit()?;
        Ok(true)
    }

    pub fn habits(&self) -> StoreResult<Vec<StoredHabit>> {
        let mut statement = self.connection.prepare(
            "SELECT id, title, schedule_type, weekdays, target_quantity, unit, time_zone,
                paused_at, version, updated_at, deleted_at
             FROM habits WHERE deleted_at IS NULL ORDER BY title, id",
        )?;
        let rows = statement.query_map([], map_habit)?;
        let mut habits = Vec::new();
        for row in rows {
            habits.push(row?);
        }
        Ok(habits)
    }

    /// Upserts the single log for (habit, date) and queues its push mutation; same-date edits
    /// reuse the local row and base version.
    pub fn upsert_habit_log(
        &mut self,
        habit_id: &str,
        log_date: &str,
        quantity: i64,
        status: &str,
        mutation_id: &str,
        now: &str,
    ) -> StoreResult<bool> {
        let date_ok = log_date.len() == 10
            && log_date.as_bytes()[4] == b'-'
            && log_date.as_bytes()[7] == b'-';
        if habit_id.is_empty()
            || !date_ok
            || !(0..=1000).contains(&quantity)
            || !matches!(status, "done" | "skipped")
            || mutation_id.is_empty()
            || now.is_empty()
        {
            return Err(StoreError::Validation("invalid habit log"));
        }
        if mutation_exists(&self.connection, mutation_id)? {
            return Ok(false);
        }
        let transaction = self.connection.transaction()?;
        let habit = transaction
            .query_row(
                "SELECT id FROM habits WHERE id = ?1 AND deleted_at IS NULL",
                params![habit_id],
                |row| row.get::<_, String>(0),
            )
            .optional()?
            .ok_or(StoreError::Validation("habit not found"))?;
        debug_assert_eq!(habit, habit_id);
        let existing: Option<(String, i64)> = transaction
            .query_row(
                "SELECT id, version FROM habit_logs WHERE habit_id = ?1 AND log_date = ?2",
                params![habit_id, log_date],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?;
        let (log_id, base_version) = match existing {
            Some((id, version)) => {
                transaction.execute(
                    "UPDATE habit_logs SET quantity = ?2, status = ?3, version = ?4, updated_at = ?5
                     WHERE id = ?1",
                    params![id, quantity, status, version + 1, now],
                )?;
                (id, version)
            }
            None => {
                let id = uuid::Uuid::new_v4().to_string();
                transaction.execute(
                    "INSERT INTO habit_logs(id, habit_id, log_date, quantity, status, version, updated_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, 1, ?6)",
                    params![id, habit_id, log_date, quantity, status, now],
                )?;
                (id, 0)
            }
        };
        let payload = serde_json::json!({
            "habit_id": habit_id,
            "log_date": log_date,
            "quantity": quantity,
            "status": status
        })
        .to_string();
        insert_outbox(
            &transaction,
            OutboxWrite {
                mutation_id,
                entity_type: "habit_log",
                entity_id: &log_id,
                operation: "upsert",
                base_version,
                payload: &payload,
                now,
            },
        )?;
        transaction.commit()?;
        Ok(true)
    }

    pub fn habit_logs(
        &self,
        habit_id: &str,
        from: &str,
        to: &str,
    ) -> StoreResult<Vec<StoredHabitLog>> {
        let mut statement = self.connection.prepare(
            "SELECT id, habit_id, log_date, quantity, status, version, updated_at
             FROM habit_logs WHERE habit_id = ?1 AND log_date BETWEEN ?2 AND ?3
             ORDER BY log_date, id",
        )?;
        let rows = statement.query_map(params![habit_id, from, to], map_habit_log)?;
        let mut logs = Vec::new();
        for row in rows {
            logs.push(row?);
        }
        Ok(logs)
    }

    /// Records a conflict together with the server entity so the user can resolve it later.
    pub fn record_conflict(
        &mut self,
        mutation_id: &str,
        entity_type: &str,
        entity_id: &str,
        server_entity: &str,
        server_version: i64,
        now: &str,
    ) -> StoreResult<()> {
        self.connection.execute(
            "INSERT INTO sync_conflicts(mutation_id, entity_type, entity_id, server_entity,
                server_version, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)
             ON CONFLICT(mutation_id) DO UPDATE SET entity_type=excluded.entity_type,
                entity_id=excluded.entity_id, server_entity=excluded.server_entity,
                server_version=excluded.server_version, created_at=excluded.created_at",
            params![
                mutation_id,
                entity_type,
                entity_id,
                server_entity,
                server_version,
                now
            ],
        )?;
        Ok(())
    }

    /// Drops conflict entries whose pending mutation is gone (resolved elsewhere or discarded).
    pub fn prune_conflicts(&self) -> StoreResult<usize> {
        Ok(self.connection.execute(
            "DELETE FROM sync_conflicts WHERE NOT EXISTS
                (SELECT 1 FROM outbox WHERE outbox.mutation_id = sync_conflicts.mutation_id)",
            [],
        )?)
    }

    pub fn conflicts(&self) -> StoreResult<Vec<ConflictEntry>> {
        let mut statement = self.connection.prepare(
            "SELECT mutation_id, entity_type, entity_id, server_entity, server_version, created_at
             FROM sync_conflicts ORDER BY created_at, mutation_id",
        )?;
        let rows = statement.query_map([], |row| {
            Ok(ConflictEntry {
                mutation_id: row.get(0)?,
                entity_type: row.get(1)?,
                entity_id: row.get(2)?,
                server_entity: row.get(3)?,
                server_version: row.get(4)?,
                created_at: row.get(5)?,
            })
        })?;
        let mut conflicts = Vec::new();
        for row in rows {
            conflicts.push(row?);
        }
        Ok(conflicts)
    }

    /// Keeps the server state, drops the local mutation and its conflict record.
    pub fn resolve_conflict_keep_server(&mut self, mutation_id: &str) -> StoreResult<bool> {
        let conflict = self
            .conflicts()?
            .into_iter()
            .find(|entry| entry.mutation_id == mutation_id);
        let Some(conflict) = conflict else {
            return Ok(false);
        };
        self.force_apply_snapshot(
            &conflict.entity_type,
            &conflict.entity_id,
            &conflict.server_entity,
            conflict.server_version,
        )?;
        self.remove_mutation(mutation_id)?;
        self.remove_conflict(mutation_id)?;
        Ok(true)
    }

    /// Retries the same mutation against the server on the next sync.
    pub fn resolve_conflict_retry(&self, mutation_id: &str) -> StoreResult<bool> {
        let changed = self.connection.execute(
            "UPDATE outbox SET attempts = 0, last_error = NULL WHERE mutation_id = ?1",
            params![mutation_id],
        )?;
        if changed == 0 {
            return Ok(false);
        }
        self.remove_conflict(mutation_id)?;
        Ok(true)
    }

    /// Rebases the mutation on the server version, overwriting it on the next sync.
    pub fn resolve_conflict_overwrite(&self, mutation_id: &str) -> StoreResult<bool> {
        let conflict = self
            .conflicts()?
            .into_iter()
            .find(|entry| entry.mutation_id == mutation_id);
        let Some(conflict) = conflict else {
            return Ok(false);
        };
        let changed = self.connection.execute(
            "UPDATE outbox SET base_version = ?2, attempts = 0, last_error = NULL
             WHERE mutation_id = ?1",
            params![mutation_id, conflict.server_version],
        )?;
        if changed == 0 {
            return Ok(false);
        }
        self.remove_conflict(mutation_id)?;
        Ok(true)
    }

    fn remove_conflict(&self, mutation_id: &str) -> StoreResult<()> {
        self.connection.execute(
            "DELETE FROM sync_conflicts WHERE mutation_id = ?1",
            params![mutation_id],
        )?;
        Ok(())
    }

    /// Forces a server snapshot into the local store (conflict resolution "server wins"),
    /// ignoring local versions and removing nothing else.
    pub fn force_apply_snapshot(
        &mut self,
        entity_type: &str,
        _entity_id: &str,
        payload: &str,
        version: i64,
    ) -> StoreResult<()> {
        let value: serde_json::Value = serde_json::from_str(payload)
            .map_err(|_| StoreError::Validation("snapshot payload is not JSON"))?;
        let transaction = self.connection.transaction()?;
        match entity_type {
            "habit" => {
                let stored: StoredHabit = serde_json::from_value(value)
                    .map_err(|_| StoreError::Validation("snapshot is not a habit"))?;
                let weekdays = stored
                    .weekdays
                    .as_ref()
                    .map(|days| serde_json::to_string(days).unwrap_or_else(|_| "[]".to_string()));
                transaction.execute(
                    "INSERT INTO habits(id, title, schedule_type, weekdays, target_quantity, unit,
                        time_zone, paused_at, version, updated_at, deleted_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
                     ON CONFLICT(id) DO UPDATE SET title = excluded.title,
                        schedule_type = excluded.schedule_type, weekdays = excluded.weekdays,
                        target_quantity = excluded.target_quantity, unit = excluded.unit,
                        time_zone = excluded.time_zone, paused_at = excluded.paused_at,
                        version = excluded.version, updated_at = excluded.updated_at,
                        deleted_at = excluded.deleted_at",
                    params![
                        stored.id,
                        stored.title,
                        stored.schedule_type,
                        weekdays,
                        stored.target_quantity,
                        stored.unit,
                        stored.time_zone,
                        stored.paused_at,
                        version,
                        stored.updated_at,
                        stored.deleted_at
                    ],
                )?;
            }
            "habit_log" => {
                let stored: StoredHabitLog = serde_json::from_value(value)
                    .map_err(|_| StoreError::Validation("snapshot is not a habit log"))?;
                transaction.execute(
                    "INSERT INTO habit_logs(id, habit_id, log_date, quantity, status, version, updated_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
                     ON CONFLICT(id) DO UPDATE SET habit_id = excluded.habit_id,
                        log_date = excluded.log_date, quantity = excluded.quantity,
                        status = excluded.status, version = excluded.version,
                        updated_at = excluded.updated_at",
                    params![
                        stored.id,
                        stored.habit_id,
                        stored.log_date,
                        stored.quantity,
                        stored.status,
                        version,
                        stored.updated_at
                    ],
                )?;
            }
            "task" => {
                let stored: StoredTask = serde_json::from_value(value)
                    .map_err(|_| StoreError::Validation("snapshot is not a task"))?;
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
                        version,
                        stored.updated_at,
                        stored.deleted_at
                    ],
                )?;
            }
            _ => return Err(StoreError::Validation("unsupported snapshot entity")),
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn set_cursor_value(&self, value: &str) -> StoreResult<()> {
        self.connection.execute(
            "INSERT INTO sync_state(key, value) VALUES ('pull_cursor', ?1)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![value],
        )?;
        Ok(())
    }

    pub fn clear_cursor(&self) -> StoreResult<()> {
        self.connection
            .execute("DELETE FROM sync_state WHERE key = 'pull_cursor'", [])?;
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

fn apply_habit_pull(
    transaction: &rusqlite::Transaction<'_>,
    change: &Change,
    outcome: &mut PullOutcome,
) -> StoreResult<()> {
    let local: Option<(i64,)> = transaction
        .query_row(
            "SELECT version FROM habits WHERE id = ?1",
            params![change.entity_id],
            |row| Ok((row.get(0)?,)),
        )
        .optional()?;
    if let Some((version,)) = local
        && version >= change.entity_version
    {
        outcome.skipped_stale += 1;
        return Ok(());
    }
    let payload: serde_json::Value = serde_json::from_str(&change.payload)
        .map_err(|_| StoreError::Validation("pull payload is not JSON"))?;
    if change.operation == "delete" {
        let updated_at = payload
            .get("updated_at")
            .and_then(|value| value.as_str())
            .unwrap_or_default();
        transaction.execute(
            "UPDATE habits SET deleted_at = COALESCE(?2, deleted_at), version = ?3 WHERE id = ?1",
            params![change.entity_id, updated_at, change.entity_version],
        )?;
        outcome.applied += 1;
        return Ok(());
    }
    let stored: StoredHabit = serde_json::from_value(payload)
        .map_err(|_| StoreError::Validation("pull payload is not a habit"))?;
    let weekdays = stored
        .weekdays
        .as_ref()
        .map(|days| serde_json::to_string(days).unwrap_or_else(|_| "[]".to_string()));
    transaction.execute(
        "INSERT INTO habits(id, title, schedule_type, weekdays, target_quantity, unit,
            time_zone, paused_at, version, updated_at, deleted_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
         ON CONFLICT(id) DO UPDATE SET title = excluded.title,
            schedule_type = excluded.schedule_type, weekdays = excluded.weekdays,
            target_quantity = excluded.target_quantity, unit = excluded.unit,
            time_zone = excluded.time_zone, paused_at = excluded.paused_at,
            version = excluded.version, updated_at = excluded.updated_at,
            deleted_at = excluded.deleted_at",
        params![
            stored.id,
            stored.title,
            stored.schedule_type,
            weekdays,
            stored.target_quantity,
            stored.unit,
            stored.time_zone,
            stored.paused_at,
            change.entity_version,
            stored.updated_at,
            stored.deleted_at
        ],
    )?;
    outcome.applied += 1;
    Ok(())
}

fn apply_habit_log_pull(
    transaction: &rusqlite::Transaction<'_>,
    change: &Change,
    outcome: &mut PullOutcome,
) -> StoreResult<()> {
    if change.operation != "upsert" {
        outcome.skipped_stale += 1;
        return Ok(());
    }
    let local: Option<(i64,)> = transaction
        .query_row(
            "SELECT version FROM habit_logs WHERE id = ?1",
            params![change.entity_id],
            |row| Ok((row.get(0)?,)),
        )
        .optional()?;
    if let Some((version,)) = local
        && version >= change.entity_version
    {
        outcome.skipped_stale += 1;
        return Ok(());
    }
    let payload: serde_json::Value = serde_json::from_str(&change.payload)
        .map_err(|_| StoreError::Validation("pull payload is not JSON"))?;
    let stored: StoredHabitLog = serde_json::from_value(payload)
        .map_err(|_| StoreError::Validation("pull payload is not a habit log"))?;
    transaction.execute(
        "INSERT INTO habit_logs(id, habit_id, log_date, quantity, status, version, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
         ON CONFLICT(id) DO UPDATE SET habit_id = excluded.habit_id,
            log_date = excluded.log_date, quantity = excluded.quantity,
            status = excluded.status, version = excluded.version,
            updated_at = excluded.updated_at",
        params![
            stored.id,
            stored.habit_id,
            stored.log_date,
            stored.quantity,
            stored.status,
            change.entity_version,
            stored.updated_at
        ],
    )?;
    outcome.applied += 1;
    Ok(())
}

fn mutation_exists(connection: &Connection, mutation_id: &str) -> StoreResult<bool> {
    Ok(connection
        .query_row(
            "SELECT 1 FROM outbox WHERE mutation_id = ?1",
            params![mutation_id],
            |_| Ok(()),
        )
        .optional()?
        .is_some())
}

fn map_habit(row: &rusqlite::Row<'_>) -> rusqlite::Result<StoredHabit> {
    let weekdays: Option<String> = row.get(3)?;
    Ok(StoredHabit {
        id: row.get(0)?,
        title: row.get(1)?,
        schedule_type: row.get(2)?,
        weekdays: weekdays.and_then(|value| serde_json::from_str(&value).ok()),
        target_quantity: row.get(4)?,
        unit: row.get(5)?,
        time_zone: row.get(6)?,
        paused_at: row.get(7)?,
        version: row.get(8)?,
        updated_at: row.get(9)?,
        deleted_at: row.get(10)?,
    })
}

fn map_habit_log(row: &rusqlite::Row<'_>) -> rusqlite::Result<StoredHabitLog> {
    Ok(StoredHabitLog {
        id: row.get(0)?,
        habit_id: row.get(1)?,
        log_date: row.get(2)?,
        quantity: row.get(3)?,
        status: row.get(4)?,
        version: row.get(5)?,
        updated_at: row.get(6)?,
    })
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
