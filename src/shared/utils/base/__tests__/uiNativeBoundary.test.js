const runtime = vi.hoisted(() => ({
    web: false,
    desktopIntegration: true,
    customRendererExtensions: true
}));

vi.mock('../../../../plugins/router', () => ({ router: { push: vi.fn() } }));
vi.mock('../../../../services/config.js', () => ({
    default: { setString: vi.fn(), getString: vi.fn() }
}));
vi.mock('../../../../web/platform.js', () => ({
    get isWebPlatform() {
        return runtime.web;
    }
}));
vi.mock('../../../../services/runtimeCapabilities.js', () => ({
    runtimeCapabilities: runtime
}));

import { changeAppThemeStyle, refreshCustomCss, refreshCustomScript } from '../ui';

describe('UI native boundaries', () => {
    const appApiDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'AppApi');

    beforeEach(() => {
        runtime.web = false;
        runtime.desktopIntegration = true;
        runtime.customRendererExtensions = true;
        Reflect.deleteProperty(globalThis, 'AppApi');
    });

    afterEach(() => {
        if (appApiDescriptor) Object.defineProperty(globalThis, 'AppApi', appApiDescriptor);
        document.getElementById('app-theme-mode-style')?.remove();
        document.getElementById('app-custom-style')?.remove();
        document.getElementById('app-custom-script')?.remove();
        document.documentElement.removeAttribute('data-theme');
        document.documentElement.classList.remove('dark');
    });

    it('applies browser theme changes without an AppApi global', () => {
        runtime.web = true;

        expect(changeAppThemeStyle('midnight')).toEqual({ isDark: true });
        expect(document.documentElement.dataset.theme).toBe('midnight');
        expect(document.documentElement.classList.contains('dark')).toBe(true);
        expect(document.getElementById('app-theme-mode-style').getAttribute('rel')).toBe('stylesheet');
        expect(document.getElementById('app-theme-mode-style').getAttribute('href')).toBeTruthy();

        expect(changeAppThemeStyle('light')).toEqual({ isDark: false });
        expect(document.documentElement.dataset.theme).toBe('light');
        expect(document.documentElement.classList.contains('dark')).toBe(false);
        expect(document.getElementById('app-theme-mode-style')).toBeNull();
    });

    it.each([
        ['light', 0],
        ['dark', 1],
        ['midnight', 2]
    ])('keeps the desktop native theme mapping for %s', (theme, nativeTheme) => {
        const ChangeTheme = vi.fn();
        globalThis.AppApi = { ChangeTheme };

        changeAppThemeStyle(theme);

        expect(ChangeTheme).toHaveBeenCalledExactlyOnceWith(nativeTheme);
    });

    it('does not change the native window theme in the headless worker', () => {
        runtime.desktopIntegration = false;

        expect(changeAppThemeStyle('dark')).toEqual({ isDark: true });
        expect(document.documentElement.classList.contains('dark')).toBe(true);
    });

    it.each(['browser', 'headless worker'])('skips custom native extensions in the %s', async (platform) => {
        runtime.web = platform === 'browser';
        runtime.customRendererExtensions = platform !== 'headless worker';
        const style = document.createElement('link');
        style.id = 'app-custom-style';
        const script = document.createElement('script');
        script.id = 'app-custom-script';
        document.head.append(style, script);

        await expect(refreshCustomCss()).resolves.toBeUndefined();
        await expect(refreshCustomScript()).resolves.toBeUndefined();

        expect(document.getElementById('app-custom-style')).toBeNull();
        expect(document.getElementById('app-custom-script')).toBeNull();
    });

    it('keeps custom extension loading available on desktop', async () => {
        const CustomCss = vi.fn().mockResolvedValue('');
        const CustomScript = vi.fn().mockResolvedValue('');
        globalThis.AppApi = { CustomCss, CustomScript };

        await refreshCustomCss();
        await refreshCustomScript();

        expect(CustomCss).toHaveBeenCalledOnce();
        expect(CustomScript).toHaveBeenCalledOnce();
    });
});
