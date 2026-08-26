#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# 文件: scripts/ck-panel.sh
# 作用: CommunicationKernel Host.App 的命令行管理面板（树莓派 / Linux）。
#
# 安装:
#   sudo cp ck-panel.sh /usr/local/bin/ck && sudo chmod +x /usr/local/bin/ck
#   之后在任意目录敲  ck  即可打开。
#
# 设计约定:
#   1) 只依赖 bash + coreutils + systemd + python3（Raspberry Pi OS 全部自带），
#      不要求 jq / curl，现场机器往往是最小化安装。
#   2) 改 JSON 一律走 python3 的 json 模块，绝不用 sed 拼字符串——
#      sed 改 JSON 在缩进或转义上稍有出入就会写出语法错误的文件，
#      而宿主启动时只会报一句 "配置无效"，看不出是被改坏的。
#   3) 所有破坏性操作（卸载、回滚、覆盖配置）一律二次确认。
#   4) 每次改配置前自动备份，备份保留在同目录的 .bak.<时间戳>。
# -----------------------------------------------------------------------------

set -o pipefail

# ============================================================================
# 全局常量
# ============================================================================

INSTALL_DIR="/opt/communication-kernel"
SERVICE_NAME="communication-kernel"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
EXE_NAME="CommunicationKernel.Host.App"
APPSETTINGS="${INSTALL_DIR}/appsettings.json"
STAGING_DIR="/tmp/ck-new"
BACKUP_DIR="${INSTALL_DIR}.bak"

# 颜色。非 TTY（管道、CI）时全部置空，避免转义码污染日志
if [ -t 1 ]; then
    C_RESET=$'\e[0m';  C_BOLD=$'\e[1m';   C_DIM=$'\e[2m'
    C_RED=$'\e[31m';   C_GREEN=$'\e[32m'; C_YELLOW=$'\e[33m'
    C_BLUE=$'\e[36m';  C_GRAY=$'\e[90m'
else
    C_RESET=; C_BOLD=; C_DIM=; C_RED=; C_GREEN=; C_YELLOW=; C_BLUE=; C_GRAY=
fi

# ============================================================================
# 基础输出
# ============================================================================

ok()   { printf '%s✔ %s%s\n' "$C_GREEN"  "$*" "$C_RESET"; }
warn() { printf '%s! %s%s\n' "$C_YELLOW" "$*" "$C_RESET"; }
err()  { printf '%s✘ %s%s\n' "$C_RED"    "$*" "$C_RESET"; }
info() { printf '%s  %s%s\n' "$C_GRAY"   "$*" "$C_RESET"; }

# 等待用户看完再回菜单。没有这个，输出会被菜单立刻刷掉
pause() {
    printf '\n%s按回车返回菜单…%s' "$C_DIM" "$C_RESET"
    read -r _
}

# 二次确认。默认为否——手滑直接回车不应该触发破坏性操作
confirm() {
    local prompt="$1" reply
    printf '%s%s [y/N]: %s' "$C_YELLOW" "$prompt" "$C_RESET"
    read -r reply
    [ "$reply" = "y" ] || [ "$reply" = "Y" ]
}

# 需要 root 的操作统一从这里进，避免每个函数各写一遍判断
need_root() {
    if [ "$(id -u)" -ne 0 ]; then
        err "该操作需要 root 权限，请用 sudo ck 重新运行。"
        return 1
    fi
    return 0
}

# ============================================================================
# 状态探测
# ============================================================================

# 服务是否已安装（单元文件存在即视为已安装）
is_installed() { [ -f "$UNIT_FILE" ]; }

# 服务是否正在运行
is_running() { systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; }

# 是否开机自启
is_enabled() { systemctl is-enabled --quiet "$SERVICE_NAME" 2>/dev/null; }

# 从 appsettings.json 读出监听地址。文件缺失或解析失败时回显提示而非报错退出
current_url() {
    if [ ! -f "$APPSETTINGS" ]; then
        echo "(appsettings.json 不存在)"
        return
    fi
    python3 - "$APPSETTINGS" <<'PY' 2>/dev/null || echo "(解析失败)"
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8-sig') as f:
        cfg = json.load(f)
    print(cfg.get('Kestrel', {}).get('Endpoints', {}).get('Grpc', {}).get('Url', '(未配置)'))
except Exception:
    raise SystemExit(1)
PY
}

# 从监听地址里抽出端口，供端口占用检查使用
current_port() {
    current_url | sed -n 's#.*:\([0-9]\{1,5\}\)/*$#\1#p'
}

# ============================================================================
# 菜单
# ============================================================================

print_header() {
    local run_txt enable_txt url

    if ! is_installed; then
        run_txt="${C_GRAY}未安装${C_RESET}"
        enable_txt="${C_GRAY}—${C_RESET}"
    elif is_running; then
        run_txt="${C_GREEN}运行中${C_RESET}"
        enable_txt=$(is_enabled && echo "${C_GREEN}已启用${C_RESET}" || echo "${C_YELLOW}未启用${C_RESET}")
    else
        run_txt="${C_RED}已停止${C_RESET}"
        enable_txt=$(is_enabled && echo "${C_GREEN}已启用${C_RESET}" || echo "${C_YELLOW}未启用${C_RESET}")
    fi

    url=$(current_url)

    clear
    printf '%s' "$C_BLUE"
    echo "================== CommunicationKernel 管理面板 =================="
    printf '%s' "$C_RESET"
    printf '  服务状态: %b    开机自启: %b\n' "$run_txt" "$enable_txt"
    printf '  监听地址: %s%s%s\n' "$C_BOLD" "$url" "$C_RESET"
    printf '  安装目录: %s%s%s\n' "$C_GRAY" "$INSTALL_DIR" "$C_RESET"
    printf '%s' "$C_BLUE"
    echo "=================================================================="
    printf '%s\n' "$C_RESET"
}

print_menu() {
    printf '%s─ 服务 ─%s\n' "$C_DIM" "$C_RESET"
    echo "  (1)  启动服务                    (4)  查看服务状态"
    echo "  (2)  停止服务                    (5)  设置开机自启"
    echo "  (3)  重启服务                    (6)  取消开机自启"
    echo
    printf '%s─ 配置 ─%s\n' "$C_DIM" "$C_RESET"
    echo "  (10) 修改监听 IP 与端口          (13) 编辑 appsettings.json"
    echo "  (11) 只改端口                    (14) 恢复备份的配置"
    echo "  (12) 查看当前完整配置"
    echo
    printf '%s─ 诊断 ─%s\n' "$C_DIM" "$C_RESET"
    echo "  (20) 实时日志                    (24) 检查插件加载情况"
    echo "  (21) 最近 50 条错误              (25) 串口设备与权限检查"
    echo "  (22) 端口监听检查                (26) 一键体检"
    echo "  (23) 防火墙状态"
    echo
    printf '%s─ 部署 ─%s\n' "$C_DIM" "$C_RESET"
    echo "  (30) 安装/重装 systemd 服务      (33) 卸载"
    echo "  (31) 升级（从 ${STAGING_DIR}）"
    echo "  (32) 回滚到上一版本"
    echo
    echo "  (0)  退出"
    echo
}

# ============================================================================
# 服务管理
# ============================================================================

svc_start() {
    need_root || return
    systemctl start "$SERVICE_NAME" && ok "服务已启动" || err "启动失败，用 (21) 看错误日志"
}

svc_stop() {
    need_root || return
    # 停服务会中断所有 PLC 通讯，必须确认
    confirm "停止服务会中断全部 PLC 通讯，确定吗？" || { info "已取消"; return; }
    systemctl stop "$SERVICE_NAME" && ok "服务已停止" || err "停止失败"
}

svc_restart() {
    need_root || return
    systemctl restart "$SERVICE_NAME" && ok "服务已重启" || err "重启失败，用 (21) 看错误日志"
}

svc_status() {
    systemctl status "$SERVICE_NAME" --no-pager -l
}

svc_enable() {
    need_root || return
    systemctl enable "$SERVICE_NAME" && ok "已设为开机自启" || err "设置失败"
}

svc_disable() {
    need_root || return
    systemctl disable "$SERVICE_NAME" && ok "已取消开机自启" || err "设置失败"
}

# ============================================================================
# 配置
# ============================================================================

# 备份 appsettings.json。改配置前必调，带时间戳便于回溯
backup_appsettings() {
    [ -f "$APPSETTINGS" ] || return 0
    local stamp; stamp=$(date +%Y%m%d-%H%M%S)
    cp "$APPSETTINGS" "${APPSETTINGS}.bak.${stamp}" && info "已备份到 appsettings.json.bak.${stamp}"
}

# 写入监听地址。python3 负责解析与回写，保证产出的仍是合法 JSON
set_url() {
    local url="$1"
    need_root || return 1

    if [ ! -f "$APPSETTINGS" ]; then
        err "找不到 $APPSETTINGS"
        return 1
    fi

    backup_appsettings

    python3 - "$APPSETTINGS" "$url" <<'PY'
import json, sys

path, url = sys.argv[1], sys.argv[2]

# utf-8-sig：VS 写出来的 json 可能带 BOM，用 utf-8 读会在首字符处报错
with open(path, encoding='utf-8-sig') as f:
    cfg = json.load(f)

# setdefault 逐层建结构：现场可能拿到一份被简化过的配置，不能假设层级都在
kestrel  = cfg.setdefault('Kestrel', {})
endpoints = kestrel.setdefault('Endpoints', {})
grpc = endpoints.setdefault('Grpc', {})
grpc['Url'] = url

# Protocols 必须是 Http2。明文端点没有 TLS ALPN 可协商，
# 配成 Http1AndHttp2 会让 Kestrel 退回 HTTP/1.1，
# 此后所有 gRPC 调用被以 HTTP_1_1_REQUIRED 拒绝——这个坑踩过一次。
grpc['Protocols'] = 'Http2'

with open(path, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, indent=2, ensure_ascii=False)
    f.write('\n')
PY

    if [ $? -eq 0 ]; then
        ok "监听地址已改为 $url"
        if is_running && confirm "需要重启服务才生效，现在重启吗？"; then
            systemctl restart "$SERVICE_NAME" && ok "服务已重启"
        else
            warn "配置已改但未重启，当前仍在用旧地址"
        fi
    else
        err "写入失败，配置未改动"
    fi
}

cfg_set_url() {
    local ip port
    printf '监听 IP（0.0.0.0 表示允许远端访问，localhost 仅本机）: '
    read -r ip
    [ -z "$ip" ] && { info "已取消"; return; }

    printf '端口 [5000]: '
    read -r port
    [ -z "$port" ] && port=5000

    # 端口必须是 1-65535 的数字，否则写进去服务直接起不来
    if ! printf '%s' "$port" | grep -qE '^[0-9]{1,5}$' || [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
        err "端口不合法：$port"
        return
    fi

    if [ "$ip" = "0.0.0.0" ]; then
        warn "绑定 0.0.0.0 意味着整个网段都能读写 PLC——gRPC 端点没有任何认证。"
        warn "请确认已用防火墙按网段放行（菜单 23 可查看防火墙状态）。"
    fi

    set_url "http://${ip}:${port}"
}

cfg_set_port() {
    local url port host
    url=$(current_url)

    # 从现有地址里保留 scheme 与主机部分，只换端口
    host=$(printf '%s' "$url" | sed -n 's#^\(http://[^:/]*\).*#\1#p')
    if [ -z "$host" ]; then
        err "当前地址无法解析（$url），请用 (10) 完整设置。"
        return
    fi

    printf '新端口（当前 %s）: ' "$(current_port)"
    read -r port
    [ -z "$port" ] && { info "已取消"; return; }

    if ! printf '%s' "$port" | grep -qE '^[0-9]{1,5}$' || [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
        err "端口不合法：$port"
        return
    fi

    set_url "${host}:${port}"
}

cfg_show() {
    if [ -f "$APPSETTINGS" ]; then
        printf '%s%s%s\n' "$C_DIM" "$APPSETTINGS" "$C_RESET"
        cat "$APPSETTINGS"
    else
        err "找不到 $APPSETTINGS"
    fi
}

cfg_edit() {
    need_root || return
    local editor="${EDITOR:-nano}"
    command -v "$editor" >/dev/null 2>&1 || editor=vi

    backup_appsettings
    "$editor" "$APPSETTINGS"

    # 手工编辑最常见的事故就是 JSON 语法错误，存盘后立刻校验
    if python3 -c "import json,sys; json.load(open(sys.argv[1],encoding='utf-8-sig'))" "$APPSETTINGS" 2>/dev/null; then
        ok "JSON 语法检查通过"
        if is_running && confirm "重启服务使配置生效？"; then
            systemctl restart "$SERVICE_NAME" && ok "服务已重启"
        fi
    else
        err "JSON 语法错误！服务将无法启动。"
        warn "用 (14) 可以恢复到最近一次备份。"
    fi
}

cfg_restore() {
    need_root || return
    local backups i=1 choice files=()

    # 按时间倒序列出备份，最新的排在最前
    while IFS= read -r f; do files+=("$f"); done < <(ls -1t "${APPSETTINGS}".bak.* 2>/dev/null)

    if [ ${#files[@]} -eq 0 ]; then
        info "没有找到任何备份"
        return
    fi

    echo "可用备份："
    for f in "${files[@]}"; do
        printf '  (%d) %s\n' "$i" "$(basename "$f")"
        i=$((i + 1))
    done

    printf '选择编号（回车取消）: '
    read -r choice
    [ -z "$choice" ] && { info "已取消"; return; }

    if ! printf '%s' "$choice" | grep -qE '^[0-9]+$' || [ "$choice" -lt 1 ] || [ "$choice" -gt ${#files[@]} ]; then
        err "编号不合法"
        return
    fi

    cp "${files[$((choice - 1))]}" "$APPSETTINGS" && ok "已恢复"
    is_running && confirm "重启服务使配置生效？" && systemctl restart "$SERVICE_NAME" && ok "服务已重启"
}

# ============================================================================
# 诊断
# ============================================================================

diag_follow_log() {
    info "Ctrl+C 退出日志跟随"
    journalctl -u "$SERVICE_NAME" -f --no-pager
}

diag_errors() {
    journalctl -u "$SERVICE_NAME" -p err -n 50 --no-pager
}

diag_port() {
    local port; port=$(current_port)

    if [ -z "$port" ]; then
        err "无法从配置中解析端口"
        return
    fi

    echo "配置端口: $port"
    echo

    if ss -tlnp 2>/dev/null | grep -q ":${port} "; then
        ok "端口 $port 正在监听："
        ss -tlnp 2>/dev/null | grep ":${port} "
        echo

        # 绑 127.0.0.1 是远端连不上的头号原因，单独点出来
        if ss -tln 2>/dev/null | grep ":${port} " | grep -q '127.0.0.1'; then
            warn "只绑在 127.0.0.1，远端电脑连不上。用 (10) 改成 0.0.0.0。"
        fi
    else
        err "端口 $port 没有在监听——服务可能没起来，或起来后崩了"
        info "用 (4) 看服务状态，(21) 看错误日志"
    fi
}

diag_firewall() {
    if command -v ufw >/dev/null 2>&1; then
        ufw status verbose
    elif command -v firewall-cmd >/dev/null 2>&1; then
        firewall-cmd --list-all
    else
        info "未检测到 ufw 或 firewalld，可能未装防火墙（此时端口对整个网络开放）"
    fi
}

diag_plugins() {
    local dir="${INSTALL_DIR}/plugins"

    if [ ! -d "$dir" ]; then
        err "插件目录不存在：$dir"
        info "协议列表会是空的。检查发布产物是否包含 plugins/"
        return
    fi

    echo "插件目录内容："
    ls -1 "$dir"/*.dll 2>/dev/null | while read -r f; do printf '  %s\n' "$(basename "$f")"; done
    echo

    # 共享契约泄漏是"协议列表为空"的头号原因，且不抛任何异常
    local leaked=0
    for c in Core.Abstractions Core.Protocol Core.Transport Plugin.Context; do
        if [ -f "${dir}/CommunicationKernel.${c}.dll" ]; then
            err "共享契约泄漏进插件目录：CommunicationKernel.${c}.dll"
            leaked=1
        fi
    done

    if [ "$leaked" -eq 1 ]; then
        warn "插件会加载到自己那份类型，与宿主的类型不互认，导致全部静默注册失败。"
        warn "解决：删掉 plugins/ 下的这些契约 DLL，它们只应存在于主目录。"
    else
        ok "四个共享契约均不在插件目录（正确）"
    fi
    echo

    # 运行期的实际结果以日志为准——上面只是静态检查
    local loaded
    loaded=$(journalctl -u "$SERVICE_NAME" --no-pager 2>/dev/null | grep -o '已加载 [0-9]* 个协议' | tail -1)
    if [ -n "$loaded" ]; then
        ok "最近一次启动：$loaded"
    else
        info "日志中未找到加载记录，服务可能尚未启动过"
    fi
}

diag_serial() {
    echo "串口设备："
    local found=0
    for p in /dev/ttyUSB* /dev/ttyACM* /dev/ttyAMA* /dev/ttyS[0-9]*; do
        [ -e "$p" ] || continue
        printf '  %s\n' "$(ls -l "$p" | awk '{print $1, $3, $4, $NF}')"
        found=1
    done
    [ "$found" -eq 0 ] && info "  未发现任何串口设备"
    echo

    if [ -d /dev/serial/by-id ]; then
        echo "稳定路径（推荐在设备配置里用这个，重启后不会变）："
        ls -1 /dev/serial/by-id/ 2>/dev/null | while read -r f; do printf '  /dev/serial/by-id/%s\n' "$f"; done
        echo
    fi

    # 服务以哪个用户跑，就要检查哪个用户的组，不能想当然用当前登录用户
    local svc_user
    svc_user=$(grep -oP '^User=\K.*' "$UNIT_FILE" 2>/dev/null)
    svc_user="${svc_user:-$(id -un)}"

    if id -nG "$svc_user" 2>/dev/null | grep -qw dialout; then
        ok "服务用户 $svc_user 属于 dialout 组"
    else
        err "服务用户 $svc_user 不在 dialout 组——打不开任何串口"
        info "修复：sudo usermod -aG dialout $svc_user，然后重启服务"
    fi
}

diag_health() {
    echo "======== 一键体检 ========"
    echo

    printf '1. 安装目录        '
    [ -d "$INSTALL_DIR" ] && ok "存在" || err "不存在：$INSTALL_DIR"

    printf '2. 可执行文件      '
    if [ -x "${INSTALL_DIR}/${EXE_NAME}" ]; then
        ok "存在且可执行"
    elif [ -f "${INSTALL_DIR}/${EXE_NAME}" ]; then
        err "存在但没有执行权限 → sudo chmod +x ${INSTALL_DIR}/${EXE_NAME}"
    else
        err "不存在"
    fi

    printf '3. systemd 单元    '
    is_installed && ok "已安装" || err "未安装（用菜单 30 安装）"

    printf '4. 服务运行        '
    is_running && ok "运行中" || err "未运行"

    printf '5. 开机自启        '
    is_enabled && ok "已启用" || warn "未启用——断电重启后不会自动起来"

    printf '6. 监听地址        '
    local url; url=$(current_url)
    case "$url" in
        *0.0.0.0*)   ok "$url（远端可访问）" ;;
        *localhost*|*127.0.0.1*) warn "$url —— 只有本机能连，远端电脑连不上" ;;
        *)           info "$url" ;;
    esac

    printf '7. 端口监听        '
    local port; port=$(current_port)
    if [ -n "$port" ] && ss -tln 2>/dev/null | grep -q ":${port} "; then
        ok "端口 $port 在监听"
    else
        err "端口 ${port:-?} 未监听"
    fi

    printf '8. 插件目录        '
    if [ -d "${INSTALL_DIR}/plugins" ]; then
        local n; n=$(ls -1 "${INSTALL_DIR}/plugins"/*.dll 2>/dev/null | wc -l)
        [ "$n" -gt 0 ] && ok "$n 个 DLL" || err "目录存在但没有 DLL"
    else
        err "不存在——协议列表会是空的"
    fi

    printf '9. 契约未泄漏      '
    local leaked=0
    for c in Core.Abstractions Core.Protocol Core.Transport Plugin.Context; do
        [ -f "${INSTALL_DIR}/plugins/CommunicationKernel.${c}.dll" ] && leaked=1
    done
    [ "$leaked" -eq 0 ] && ok "正确" || err "契约泄漏进 plugins/，插件会全部静默注册失败"

    echo
    echo "=========================="
}

# ============================================================================
# 部署
# ============================================================================

deploy_install_unit() {
    need_root || return

    if [ ! -f "${INSTALL_DIR}/${EXE_NAME}" ]; then
        err "找不到 ${INSTALL_DIR}/${EXE_NAME}，请先把发布产物放到该目录。"
        return
    fi

    local run_user
    printf '服务以哪个用户运行 [pi]: '
    read -r run_user
    [ -z "$run_user" ] && run_user=pi

    if ! id "$run_user" >/dev/null 2>&1; then
        err "用户不存在：$run_user"
        return
    fi

    is_installed && { confirm "单元文件已存在，覆盖吗？" || { info "已取消"; return; }; }

    cat > "$UNIT_FILE" <<UNIT
[Unit]
Description=CommunicationKernel Host.App
# network-online 而非 network：仅 network 只保证网络栈起来了，
# 不保证拿到地址，绑定固定 IP 时会启动失败
After=network-online.target
Wants=network-online.target

[Service]
# simple 而非 notify：宿主未引入 Microsoft.Extensions.Hosting.Systemd，
# 不会发送就绪通知，用 notify 会让 systemd 一直等到超时判定启动失败
Type=simple
WorkingDirectory=${INSTALL_DIR}
ExecStart=${INSTALL_DIR}/${EXE_NAME}
Restart=always
RestartSec=5

User=${run_user}
# 必须属于 dialout，否则打不开串口
SupplementaryGroups=dialout

Environment=DOTNET_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal
SyslogIdentifier=comm-kernel

[Install]
WantedBy=multi-user.target
UNIT

    chmod 644 "$UNIT_FILE"
    systemctl daemon-reload
    ok "单元文件已写入 $UNIT_FILE"

    confirm "现在设为开机自启并启动？" && {
        systemctl enable --now "$SERVICE_NAME" && ok "服务已启动并设为自启" \
            || err "启动失败，用 (21) 看错误日志"
    }
}

deploy_upgrade() {
    need_root || return

    if [ ! -d "$STAGING_DIR" ]; then
        err "找不到暂存目录 $STAGING_DIR"
        info "先把新版本传过来：scp -r publish/* pi@<IP>:${STAGING_DIR}"
        return
    fi

    if [ ! -f "${STAGING_DIR}/${EXE_NAME}" ]; then
        err "${STAGING_DIR} 里没有 ${EXE_NAME}，看着不像一份完整的发布产物"
        return
    fi

    confirm "升级会停止服务并替换 ${INSTALL_DIR}，确定吗？" || { info "已取消"; return; }

    # 旧版本整体挪走而不是就地覆盖：就地覆盖时新旧文件会混在一起，
    # 上一版有、这一版删掉的 DLL 会留下来，回滚也就无从谈起
    [ -d "$BACKUP_DIR" ] && rm -rf "$BACKUP_DIR"

    systemctl stop "$SERVICE_NAME" 2>/dev/null
    mv "$INSTALL_DIR" "$BACKUP_DIR" && info "旧版本已备份到 $BACKUP_DIR"

    mkdir -p "$INSTALL_DIR"
    cp -r "${STAGING_DIR}"/* "$INSTALL_DIR"/

    # 配置不跟着新版本走——里面有现场改过的监听地址
    if [ -f "${BACKUP_DIR}/appsettings.json" ]; then
        cp "${BACKUP_DIR}/appsettings.json" "${INSTALL_DIR}/appsettings.json"
        ok "已保留原有 appsettings.json"
    fi

    local run_user
    run_user=$(grep -oP '^User=\K.*' "$UNIT_FILE" 2>/dev/null)
    run_user="${run_user:-pi}"
    chown -R "${run_user}:${run_user}" "$INSTALL_DIR"
    chmod +x "${INSTALL_DIR}/${EXE_NAME}"

    systemctl start "$SERVICE_NAME"
    sleep 2

    if is_running; then
        ok "升级完成，服务已启动"
        info "确认稳定后可删除备份：sudo rm -rf $BACKUP_DIR"
    else
        err "升级后服务起不来！"
        confirm "立即回滚到上一版本？" && deploy_rollback_silent
    fi
}

# 回滚的实际动作，供菜单与升级失败时复用
deploy_rollback_silent() {
    systemctl stop "$SERVICE_NAME" 2>/dev/null
    rm -rf "$INSTALL_DIR"
    mv "$BACKUP_DIR" "$INSTALL_DIR"
    systemctl start "$SERVICE_NAME"
    sleep 2
    is_running && ok "已回滚到上一版本，服务已启动" || err "回滚后仍起不来，用 (21) 看日志"
}

deploy_rollback() {
    need_root || return

    if [ ! -d "$BACKUP_DIR" ]; then
        err "没有可回滚的备份（$BACKUP_DIR 不存在）"
        return
    fi

    confirm "回滚会丢弃当前版本，恢复到 $BACKUP_DIR，确定吗？" || { info "已取消"; return; }
    deploy_rollback_silent
}

deploy_uninstall() {
    need_root || return

    warn "卸载会删除服务与 ${INSTALL_DIR} 下的全部文件。"
    info "设备与变量配置存在上位机电脑上，不在这里，卸载不会丢配置。"
    confirm "确定卸载吗？" || { info "已取消"; return; }
    confirm "再确认一次：真的要删除 ${INSTALL_DIR} 吗？" || { info "已取消"; return; }

    systemctl disable --now "$SERVICE_NAME" 2>/dev/null
    rm -f "$UNIT_FILE"
    systemctl daemon-reload
    rm -rf "$INSTALL_DIR"
    ok "已卸载"
    info "备份目录 $BACKUP_DIR 未删除，如不需要请手动清理"
}

# ============================================================================
# 主循环
# ============================================================================

main() {
    while true; do
        print_header
        print_menu
        printf '请输入命令编号: '
        read -r choice

        case "$choice" in
            1)  svc_start;            pause ;;
            2)  svc_stop;             pause ;;
            3)  svc_restart;          pause ;;
            4)  svc_status;           pause ;;
            5)  svc_enable;           pause ;;
            6)  svc_disable;          pause ;;

            10) cfg_set_url;          pause ;;
            11) cfg_set_port;         pause ;;
            12) cfg_show;             pause ;;
            13) cfg_edit;             pause ;;
            14) cfg_restore;          pause ;;

            20) diag_follow_log            ;;  # 自带 Ctrl+C 退出，不需要 pause
            21) diag_errors;          pause ;;
            22) diag_port;            pause ;;
            23) diag_firewall;        pause ;;
            24) diag_plugins;         pause ;;
            25) diag_serial;          pause ;;
            26) diag_health;          pause ;;

            30) deploy_install_unit;  pause ;;
            31) deploy_upgrade;       pause ;;
            32) deploy_rollback;      pause ;;
            33) deploy_uninstall;     pause ;;

            0)  clear; exit 0 ;;
            "") ;;                              # 直接回车：重绘菜单
            *)  err "无效编号：$choice"; pause ;;
        esac
    done
}

main "$@"
