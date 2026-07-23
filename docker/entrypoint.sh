#!/usr/bin/env sh
set -eu

# 修改入口状态协议时必须递增；应用只会用更高版本覆盖容器入口，避免持久旧程序降级新镜像脚本。
# 镜像升级时还会依据 version.txt 选择更新后的镜像程序。
ENTRYPOINT_PROTOCOL_VERSION=2
APP_ENTRY="${TELEGRAM_PANEL_APP_ENTRY:-TelegramPanel.Web.dll}"
DATA_DIR="${TELEGRAM_PANEL_DATA_DIR:-/data}"
DEFAULT_APP_DIR="${TELEGRAM_PANEL_DEFAULT_APP_DIR:-/app}"
UPDATED_APP_DIR="${TELEGRAM_PANEL_UPDATED_APP_DIR:-$DATA_DIR/app-current}"
BACKUP_APP_DIR="${TELEGRAM_PANEL_BACKUP_APP_DIR:-$DATA_DIR/app-previous}"
DOTNET_COMMAND="${TELEGRAM_PANEL_DOTNET_COMMAND:-dotnet}"

UPDATED_APP_MARKER="$UPDATED_APP_DIR/.telegram-panel-self-update"
UPDATE_PENDING_MARKER="$UPDATED_APP_DIR/.telegram-panel-update-pending"
UPDATE_ATTEMPTED_MARKER="$UPDATED_APP_DIR/.telegram-panel-update-attempted"
UPDATE_CONFIRMED_MARKER="$UPDATED_APP_DIR/.telegram-panel-update-confirmed"
IMAGE_VERSION_FILE="$DEFAULT_APP_DIR/version.txt"
UPDATED_VERSION_FILE="$UPDATED_APP_DIR/version.txt"
UPDATE_MODE_SOURCE="${TELEGRAM_PANEL_UPDATE_MODE:-auto}"
UPDATE_MODE_FILE="$DATA_DIR/update-mode.txt"
if [ -f "$UPDATE_MODE_FILE" ]; then
  UPDATE_MODE_SOURCE="$(cat "$UPDATE_MODE_FILE")"
fi
UPDATE_MODE="$(printf '%s' "$UPDATE_MODE_SOURCE" | tr -d '\r\n' | tr '[:upper:]' '[:lower:]')"
case "$UPDATE_MODE" in
  image|binary|auto) ;;
  *) UPDATE_MODE=auto ;;
esac

log() {
  printf '[telegram-panel-entrypoint] %s\n' "$*" >&2
}

read_version_file() {
  if [ -f "$1" ]; then
    # 兼容旧发布产物可能带有 UTF-8 BOM 的版本文件，不依赖 sed 扩展语法。
    version="$(cat "$1")"
    bom="$(printf '\357\273\277')"
    case "$version" in
      "$bom"*) version="${version#"$bom"}" ;;
    esac
    printf '%s' "$version" | tr -d '\r\n'
  fi
}

version_is_greater() {
  # 发布版本是三段式语义版本；忽略 v 前缀和预发布/构建元数据。
  left="${1#v}"
  right="${2#v}"
  left="${left%%+*}"
  left="${left%%-*}"
  right="${right%%+*}"
  right="${right%%-*}"

  awk -F. '
    NR == 1 {
      if (NF != 3 || $1 !~ /^[0-9]+$/ || $2 !~ /^[0-9]+$/ || $3 !~ /^[0-9]+$/) exit 1
      left1 = $1 + 0; left2 = $2 + 0; left3 = $3 + 0
      next
    }
    NR == 2 {
      if (NF != 3 || $1 !~ /^[0-9]+$/ || $2 !~ /^[0-9]+$/ || $3 !~ /^[0-9]+$/) exit 1
      if (($1 + 0) != left1) exit (($1 + 0) > left1 ? 1 : 0)
      if (($2 + 0) != left2) exit (($2 + 0) > left2 ? 1 : 0)
      if (($3 + 0) != left3) exit (($3 + 0) > left3 ? 1 : 0)
      exit 1
    }
    { exit 1 }
  ' <<EOF
$left
$right
EOF
}

prefer_newer_image() {
  IMAGE_VERSION="$(read_version_file "$IMAGE_VERSION_FILE")"
  UPDATED_VERSION="$(read_version_file "$UPDATED_VERSION_FILE")"
  if [ -n "$IMAGE_VERSION" ] && [ -n "$UPDATED_VERSION" ] \
    && version_is_greater "$IMAGE_VERSION" "$UPDATED_VERSION"; then
    OBSOLETE_APP_DIR="$DATA_DIR/app-obsolete-$(date +%Y%m%d%H%M%S)-$$"
    if mv "$UPDATED_APP_DIR" "$OBSOLETE_APP_DIR"; then
      APP_DIR="$DEFAULT_APP_DIR"
      log "镜像版本 v$IMAGE_VERSION 高于持久化版本 v$UPDATED_VERSION，已归档旧版本并使用镜像目录"
    else
      APP_DIR="$DEFAULT_APP_DIR"
      log "无法归档旧持久化版本，为避免阻塞镜像升级，使用镜像目录：$DEFAULT_APP_DIR"
    fi
    return 0
  fi
  return 1
}

mkdir -p "$DATA_DIR" "$DATA_DIR/sessions" "$DATA_DIR/logs"
if [ ! -f "$DATA_DIR/appsettings.local.json" ]; then
  printf '{}' > "$DATA_DIR/appsettings.local.json"
fi

APP_DIR="$DEFAULT_APP_DIR"
if [ -f "$UPDATED_APP_DIR/$APP_ENTRY" ]; then
  if [ "$UPDATE_MODE" = "image" ]; then
    APP_DIR="$DEFAULT_APP_DIR"
    log "当前为 image 更新模式，使用镜像目录：$DEFAULT_APP_DIR"
  elif [ -f "$UPDATE_CONFIRMED_MARKER" ]; then
    if [ "$UPDATE_MODE" = "binary" ]; then
      APP_DIR="$UPDATED_APP_DIR"
      log "当前为 binary 更新模式，使用持久化目录：$UPDATED_APP_DIR"
    elif prefer_newer_image; then
      :
    else
      APP_DIR="$UPDATED_APP_DIR"
    fi
  elif [ -f "$UPDATE_ATTEMPTED_MARKER" ]; then
    FAILED_APP_DIR="$DATA_DIR/app-failed-$(date +%Y%m%d%H%M%S)-$$"
    log "检测到新版本上次启动未确认，正在归档失败版本：$FAILED_APP_DIR"

    if mv "$UPDATED_APP_DIR" "$FAILED_APP_DIR"; then
      if [ -f "$BACKUP_APP_DIR/$APP_ENTRY" ] && mv "$BACKUP_APP_DIR" "$UPDATED_APP_DIR"; then
        APP_DIR="$UPDATED_APP_DIR"
        log "已自动切回上一版本：$UPDATED_APP_DIR"
      else
        APP_DIR="$DEFAULT_APP_DIR"
        log "未找到可用的 app-previous，已回退到镜像内版本：$DEFAULT_APP_DIR"
      fi
    else
      APP_DIR="$DEFAULT_APP_DIR"
      log "归档失败版本失败，为避免重试坏版本，本次回退到：$DEFAULT_APP_DIR"
    fi
  elif [ -f "$UPDATE_PENDING_MARKER" ]; then
    if mv "$UPDATE_PENDING_MARKER" "$UPDATE_ATTEMPTED_MARKER"; then
      APP_DIR="$UPDATED_APP_DIR"
      log "开始首次启动新版本，已记录 attempted 状态"
    else
      log "无法记录新版本启动尝试，为安全起见继续使用：$DEFAULT_APP_DIR"
    fi
  elif [ -f "$UPDATED_APP_MARKER" ]; then
    # 兼容 v1.31.32 之前只写单个 marker 的更新器。
    # 旧 marker 也必须参与镜像版本比较，否则旧的一键更新目录会永久遮住新镜像。
    if prefer_newer_image; then
      :
    # 若目标包已经携带新版入口脚本，说明它能在 StartAsync 后确认状态；
    # 这里先补记 attempted，连“程序集加载失败、尚未进入 Program”的情况也能在下次启动回滚。
    elif [ -f "$UPDATED_APP_DIR/self-update/entrypoint.sh" ]; then
      if cp "$UPDATED_APP_MARKER" "$UPDATE_ATTEMPTED_MARKER"; then
        APP_DIR="$UPDATED_APP_DIR"
        log "已将旧版更新 marker 迁移为 attempted 状态"
      else
        log "无法迁移旧版更新 marker，为安全起见继续使用：$DEFAULT_APP_DIR"
      fi
    else
      APP_DIR="$UPDATED_APP_DIR"
    fi
  fi
fi

# 运行目录下日志统一指向持久化目录，避免更新目录轮换后日志丢失。
if [ -e "$APP_DIR/logs" ] && [ ! -L "$APP_DIR/logs" ]; then
  rm -rf "$APP_DIR/logs"
fi
if [ ! -e "$APP_DIR/logs" ]; then
  ln -s "$DATA_DIR/logs" "$APP_DIR/logs" || true
fi

# 面板保存的本地配置统一使用持久化目录。
if [ -e "$APP_DIR/appsettings.local.json" ] && [ ! -L "$APP_DIR/appsettings.local.json" ]; then
  rm -f "$APP_DIR/appsettings.local.json"
fi
if [ ! -e "$APP_DIR/appsettings.local.json" ]; then
  ln -s "$DATA_DIR/appsettings.local.json" "$APP_DIR/appsettings.local.json" || true
fi

# 历史账号记录可能保存了 sessions/<手机号>.session 这类相对路径。
# 将当前程序目录的 sessions 固定映射到持久化目录，保证切换 app-current 后旧记录仍可用。
# 若旧目录里已有文件，先只复制不覆盖地迁移，并保留原目录备份。
if [ -e "$APP_DIR/sessions" ] && [ ! -L "$APP_DIR/sessions" ]; then
  if [ -d "$APP_DIR/sessions" ]; then
    cp -an "$APP_DIR/sessions/." "$DATA_DIR/sessions/" || true
  fi
  LEGACY_SESSIONS_DIR="$APP_DIR/sessions.before-persistent"
  if [ -e "$LEGACY_SESSIONS_DIR" ]; then
    LEGACY_SESSIONS_DIR="$LEGACY_SESSIONS_DIR.$(date +%Y%m%d%H%M%S)"
  fi
  mv "$APP_DIR/sessions" "$LEGACY_SESSIONS_DIR" || true
fi
if [ ! -e "$APP_DIR/sessions" ]; then
  ln -s "$DATA_DIR/sessions" "$APP_DIR/sessions" || true
fi

cd "$APP_DIR"
exec "$DOTNET_COMMAND" "$APP_ENTRY"
