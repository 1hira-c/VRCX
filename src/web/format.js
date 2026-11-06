const ONLINE_STATUSES = new Set(['active', 'online', 'join me', 'ask me']);

export function asObject(value) {
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

export function displayName(record) {
    const value = firstString(record, ['displayName', 'name', 'username', 'targetId', 'userId', 'id']);
    return value || 'Unknown';
}

export function recordValue(record, names) {
    const direct = firstValue(asObject(record), names);
    if (direct !== undefined) return direct;

    const payload = firstValue(asObject(record?.payload), names);
    if (payload !== undefined) return payload;

    return firstValue(asObject(record?.raw), names);
}

export function firstString(record, names) {
    const value = recordValue(record, names);
    return typeof value === 'string' && value.trim() ? value : '';
}

export function isOnline(record) {
    return ONLINE_STATUSES.has(String(recordValue(record, ['status']) || '').toLowerCase());
}

export function statusLabel(value) {
    const normalized = String(value || '').toLowerCase();
    return (
        {
            active: 'オンライン',
            online: 'オンライン',
            'join me': 'Join me',
            'ask me': 'Ask me',
            busy: '取り込み中',
            offline: 'オフライン'
        }[normalized] ||
        value ||
        '状態不明'
    );
}

export function locationLabel(record) {
    return firstString(record, ['worldName', 'location', 'travelingToLocation']) || '場所は公開されていません';
}

export function formatTime(value, fallback = '記録なし') {
    if (!value) return fallback;
    const date = new Date(value);
    if (Number.isNaN(date.valueOf())) return String(value);
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

export function formatMinutes(value) {
    const minutes = Math.max(0, Number(value) || 0);
    const hours = Math.floor(minutes / 60);
    return hours ? hours + '時間' + (minutes % 60) + '分' : minutes + '分';
}

export function entityTypeFromId(id) {
    const value = String(id || '');
    if (value.startsWith('usr_')) return 'user';
    if (value.startsWith('wrld_')) return 'world';
    if (value.startsWith('avtr_')) return 'avatar';
    if (value.startsWith('grp_')) return 'group';
    return '';
}

export function typeLabel(type) {
    return (
        {
            user: 'ユーザー',
            world: 'ワールド',
            avatar: 'アバター',
            group: 'グループ'
        }[String(type || '').toLowerCase()] ||
        type ||
        '項目'
    );
}

export function tagsOf(record) {
    const value = recordValue(record, ['tags']);
    return Array.isArray(value) ? value.filter((tag) => typeof tag === 'string').slice(0, 12) : [];
}

export function safeVrchatImageUrl(value) {
    if (typeof value !== 'string' || !value) return '';
    try {
        const url = new URL(value);
        return url.protocol === 'https:' && (url.hostname === 'vrchat.cloud' || url.hostname.endsWith('.vrchat.cloud'))
            ? url.toString()
            : '';
    } catch {
        return '';
    }
}

export function syncLabel(status) {
    if (status?.authenticationRequired) return 'VRChat の再認証が必要';
    if (status?.sync?.stale) return '同期データが古くなっています';
    return (
        {
            healthy: '同期済み',
            connecting: '接続中',
            rate_limited: 'VRChat のレート制限中',
            degraded: '同期を再試行中',
            requires_authentication: 'VRChat の再認証が必要',
            not_configured: 'VRChat を接続してください'
        }[status?.sync?.state] || '状態を確認中'
    );
}

export function errorMessage(error) {
    const errorCode = error?.body?.error;
    if (errorCode === 'invalid_setup_token') return '初回確認コードが正しくありません。';
    if (errorCode === 'invalid_admin_credentials') return '管理者パスワードが正しくありません。';
    if (errorCode === 'invalid_pairing_code') return 'ペアリングコードが無効または使用済みです。';
    if (errorCode === 'invalid_recovery_code') return '復旧コードが無効または使用済みです。';
    if (errorCode === 'untrusted_request_origin')
        return 'このページの接続元を確認できませんでした。再読み込みしてください。';
    if (error?.status === 401) return 'セッションまたはVRChat認証が必要です。';
    if (error?.status === 429) return '要求が多すぎます。少し待ってから再試行してください。';
    if (
        error?.status === 503 &&
        errorCode === 'vrchat_worker_unavailable' &&
        error?.body?.reason === 'database_initialization_failed'
    )
        return 'データベースの初期化に失敗しました。ホスト上のデータベースを確認してください。';
    if (error?.status === 503 && errorCode === 'vrchat_worker_unavailable')
        return '常駐VRCXとの接続を復旧しています。認証切れとは限りません。保存済みデータは維持されています。';
    return error?.message || '要求を完了できませんでした。';
}

function firstValue(record, names) {
    for (const name of names) {
        if (Object.hasOwn(record, name) && record[name] !== null && record[name] !== undefined) {
            return record[name];
        }
    }
    return undefined;
}
