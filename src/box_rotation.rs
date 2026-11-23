use axum::{http::header, Json};
use serde_json::{json, Value};

const WEEK_MS: i64 = 7 * 24 * 60 * 60 * 1000;
const EPOCH_MS: i64 = 1_767_592_800_000; // 2026-01-05 06:00 UTC, lunes en Costa Rica.
const PLAN: [[&str; 3]; 3] = [
    ["fool", "empress", "devil"],
    ["magician", "lovers", "death"],
    ["priestess", "emperor", "fortune"],
];

fn week(index: i64) -> Value {
    let begins = EPOCH_MS + index * WEEK_MS;
    let group = index.rem_euclid(PLAN.len() as i64) as usize;
    json!({
        "startsAtMs": begins,
        "endsAtMs": begins + WEEK_MS,
        "activeBoxIds": PLAN[group],
    })
}

fn rotation(now: i64) -> Value {
    let index = (now - EPOCH_MS).div_euclid(WEEK_MS);
    json!({
        "serverNowMs": now,
        "week": week(index),
        "calendar": (index - 2..=index + 2).map(week).collect::<Vec<_>>(),
    })
}

pub async fn get() -> ([(header::HeaderName, &'static str); 1], Json<Value>) {
    (
        [(header::CACHE_CONTROL, "no-store")],
        Json(rotation(super::ahora_ms() as i64)),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn changes_at_the_exact_boundary() {
        let last = rotation(EPOCH_MS + WEEK_MS - 1);
        let next = rotation(EPOCH_MS + WEEK_MS);
        assert_eq!(last["week"]["endsAtMs"], next["week"]["startsAtMs"]);
        assert_eq!(last["calendar"][3], next["week"]);
        assert_ne!(last["week"]["activeBoxIds"], next["week"]["activeBoxIds"]);
        assert_eq!(next["week"]["activeBoxIds"].as_array().unwrap().len(), 3);
    }
}
