const STORAGE_PREFIX = 'vrcx-web:config:';

// The browser never stores desktop credentials or VRChat session material.
const FORBIDDEN_KEYS = new Set(['savedcredentials', 'lastuserloggedin']);

function storageKey(key) {
    return `${STORAGE_PREFIX}${String(key).toLowerCase()}`;
}

function isForbidden(key) {
    return FORBIDDEN_KEYS.has(String(key).toLowerCase());
}

function getStorage() {
    try {
        return globalThis.localStorage;
    } catch {
        return null;
    }
}

class WebConfigRepository {
    async init() {}

    async remove(key) {
        if (isForbidden(key)) return;
        getStorage()?.removeItem(storageKey(key));
    }

    async getString(key, defaultValue = null) {
        if (isForbidden(key)) return defaultValue;
        const value = getStorage()?.getItem(storageKey(key));
        return value === null || value === undefined || value === 'undefined' ? defaultValue : value;
    }

    async setString(key, value) {
        if (isForbidden(key)) return;
        getStorage()?.setItem(storageKey(key), String(value));
    }

    async getBool(key, defaultValue = null) {
        const value = await this.getString(key, null);
        return value === null || value === undefined ? defaultValue : value === 'true';
    }

    async setBool(key, value) {
        await this.setString(key, value ? 'true' : 'false');
    }

    async getInt(key, defaultValue = null) {
        const value = await this.getString(key, null);
        const parsed = Number.parseInt(value ?? '', 10);
        return Number.isFinite(parsed) ? parsed : defaultValue;
    }

    async setInt(key, value) {
        await this.setString(key, value);
    }

    async getFloat(key, defaultValue = null) {
        const value = await this.getString(key, null);
        const parsed = Number.parseFloat(value ?? '');
        return Number.isFinite(parsed) ? parsed : defaultValue;
    }

    async setFloat(key, value) {
        await this.setString(key, value);
    }

    async getObject(key, defaultValue = null) {
        const value = await this.getString(key, null);
        if (value === null || value === undefined) return defaultValue;
        try {
            const parsed = JSON.parse(value);
            return parsed === Object(parsed) ? parsed : defaultValue;
        } catch {
            return defaultValue;
        }
    }

    async setObject(key, value) {
        await this.setString(key, JSON.stringify(value));
    }

    async getArray(key, defaultValue = null) {
        const value = await this.getObject(key, null);
        return Array.isArray(value) ? value : defaultValue;
    }

    async setArray(key, value) {
        await this.setObject(key, value);
    }
}

export const webConfigRepository = new WebConfigRepository();
