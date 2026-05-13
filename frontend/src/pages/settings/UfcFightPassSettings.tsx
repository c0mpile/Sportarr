import { useState, useEffect } from 'react';
import {
  CheckCircleIcon,
  XCircleIcon,
  ArrowPathIcon,
  ShieldCheckIcon,
  FolderArrowDownIcon,
  BoltIcon,
  Cog6ToothIcon,
  SignalSlashIcon,
} from '@heroicons/react/24/outline';
import { toast } from 'sonner';
import { apiGet, apiPost, apiPut } from '../../utils/api';
import PageHeader from '../../components/PageHeader';
import PageShell from '../../components/PageShell';

// ─── Types ────────────────────────────────────────────────────────────────────

interface UfcSettings {
  ufcEnabled: boolean;
  ufcEmail: string;
  ufcPasswordSet: boolean;
  ufcQualityFormat: string;
  ufcConcurrentFragments: number;
  ufcOutputPath: string;
  ufcYtDlpPath: string;
  ufcCookieExists: boolean;
}

interface YtDlpStatus {
  found: boolean;
  path: string;
  version: string;
}

interface DownloadHistoryItem {
  id: number;
  downloadId: string;
  title: string;
  status: number;
  progress: number;
  errorMessage: string | null;
  added: string;
  completedAt: string | null;
  lastUpdate: string | null;
}

// ─── Quality options (label → yt-dlp format string) ─────────────────────────

const QUALITY_OPTIONS = [
  { label: 'Best',     value: 'bestvideo+bestaudio/best' },
  { label: '1080p',    value: 'bestvideo[height<=1080]+bestaudio/best[height<=1080]' },
  { label: '720p',     value: 'bestvideo[height<=720]+bestaudio/best[height<=720]' },
  { label: 'Smallest', value: 'worstvideo+worstaudio/worst' },
];

const STATUS_LABELS: Record<number, string> = {
  0: 'Queued',
  1: 'Downloading',
  2: 'Paused',
  3: 'Completed',
  4: 'Failed',
  5: 'Warning',
  6: 'Importing',
  7: 'Imported',
  8: 'Import Pending',
  9: 'Import Warning',
};

const STATUS_COLORS: Record<number, string> = {
  0: 'text-gray-400',
  1: 'text-blue-400',
  2: 'text-yellow-400',
  3: 'text-green-400',
  4: 'text-red-400',
  5: 'text-orange-400',
  6: 'text-purple-400',
  7: 'text-green-500',
  8: 'text-yellow-500',
  9: 'text-orange-500',
};

// ─── Inline form helpers ──────────────────────────────────────────────────────

function Label({ children }: { children: React.ReactNode }) {
  return <label className="block text-sm font-medium text-gray-300 mb-1.5">{children}</label>;
}

function Hint({ children }: { children: React.ReactNode }) {
  return <p className="text-xs text-gray-500 mt-1">{children}</p>;
}

function Input({
  type = 'text',
  value,
  onChange,
  placeholder,
  disabled,
}: {
  type?: string;
  value: string | number;
  onChange: (v: string) => void;
  placeholder?: string;
  disabled?: boolean;
}) {
  return (
    <input
      type={type}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      disabled={disabled}
      className="w-full px-4 py-2 bg-gray-800 border border-gray-700 rounded-lg text-white
                 placeholder-gray-500 focus:outline-none focus:border-red-600 disabled:opacity-50
                 transition-colors"
    />
  );
}

// ─── Main Component ───────────────────────────────────────────────────────────

export default function UfcFightPassSettings() {
  // ── Settings state ──
  const [settings, setSettings] = useState<UfcSettings>({
    ufcEnabled: false,
    ufcEmail: '',
    ufcPasswordSet: false,
    ufcQualityFormat: 'bestvideo+bestaudio/best',
    ufcConcurrentFragments: 4,
    ufcOutputPath: '',
    ufcYtDlpPath: '',
    ufcCookieExists: false,
  });

  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  // ── yt-dlp binary status ──
  const [ytDlpStatus, setYtDlpStatus] = useState<YtDlpStatus | null>(null);
  const [checkingBinary, setCheckingBinary] = useState(false);

  // ── Auth test ──
  const [testingAuth, setTestingAuth] = useState(false);
  const [authResult, setAuthResult] = useState<{ success: boolean; message: string } | null>(null);

  // ── Quick Download ──
  const [downloadUrl, setDownloadUrl] = useState('');
  const [customTitle, setCustomTitle] = useState('');
  const [downloading, setDownloading] = useState(false);

  // ── History ──
  const [history, setHistory] = useState<DownloadHistoryItem[]>([]);
  const [loadingHistory, setLoadingHistory] = useState(false);

  // ── Mount ──
  useEffect(() => {
    loadSettings();
    checkBinary();
    loadHistory();
  }, []);

  // ─── API Calls ─────────────────────────────────────────────────────────────

  const loadSettings = async () => {
    try {
      setLoading(true);
      const response = await apiGet('/api/ufc/settings');
      if (!response.ok) throw new Error('Failed to load UFC settings');
      const data: UfcSettings = await response.json();
      setSettings(data);
    } catch (err: any) {
      toast.error('Failed to load UFC settings', { description: err.message });
    } finally {
      setLoading(false);
    }
  };

  const checkBinary = async () => {
    try {
      setCheckingBinary(true);
      const response = await apiGet('/api/ufc/status');
      if (!response.ok) throw new Error();
      const data: YtDlpStatus = await response.json();
      setYtDlpStatus(data);
    } catch {
      setYtDlpStatus({ found: false, path: '', version: '' });
    } finally {
      setCheckingBinary(false);
    }
  };

  const loadHistory = async () => {
    try {
      setLoadingHistory(true);
      const response = await apiGet('/api/ufc/download/history?limit=20');
      if (!response.ok) return;
      const data: DownloadHistoryItem[] = await response.json();
      setHistory(data);
    } catch {
      // History is non-critical — don't block the page.
    } finally {
      setLoadingHistory(false);
    }
  };

  const handleSave = async () => {
    try {
      setSaving(true);
      const payload: Record<string, unknown> = {
        ufcEnabled:             settings.ufcEnabled,
        ufcEmail:               settings.ufcEmail,
        ufcQualityFormat:       settings.ufcQualityFormat,
        ufcConcurrentFragments: settings.ufcConcurrentFragments,
        ufcOutputPath:          settings.ufcOutputPath,
        ufcYtDlpPath:           settings.ufcYtDlpPath,
      };
      // Only send password if the user typed one.
      if (password.trim()) payload.ufcPassword = password;

      const response = await apiPut('/api/ufc/settings', payload);
      if (!response.ok) throw new Error('Save failed');
      toast.success('Settings saved', { description: 'UFC Fight Pass settings have been saved.' });
      setPassword('');
      await loadSettings(); // Refresh to get ufcPasswordSet state
    } catch (err: any) {
      toast.error('Failed to save settings', { description: err.message });
    } finally {
      setSaving(false);
    }
  };

  const handleTestAuth = async () => {
    try {
      setTestingAuth(true);
      setAuthResult(null);
      const response = await apiPost('/api/ufc/auth/test', {});
      const data: { success: boolean; message: string } = await response.json();
      setAuthResult(data);
      if (data.success) {
        toast.success('Authentication successful', { description: data.message });
        await loadSettings(); // Cookie path may have updated
      } else {
        toast.error('Authentication failed', { description: data.message });
      }
    } catch (err: any) {
      const msg = err?.message ?? 'Connection failed';
      setAuthResult({ success: false, message: msg });
      toast.error('Authentication failed', { description: msg });
    } finally {
      setTestingAuth(false);
    }
  };

  const handleStartDownload = async () => {
    if (!downloadUrl.trim()) {
      toast.warning('Enter a UFC Fight Pass URL first.');
      return;
    }
    try {
      setDownloading(true);
      const response = await apiPost('/api/ufc/download', {
        url: downloadUrl.trim(),
        customTitle: customTitle.trim() || undefined,
      });
      if (!response.ok) {
        const err = await response.json().catch(() => ({ error: 'Unknown error' }));
        throw new Error(err.error ?? 'Failed to start download');
      }
      const data: { success: boolean; downloadId: string } = await response.json();
      toast.success('Download started', {
        description: `Download ID: ${data.downloadId}. Track progress in Activity → Queue.`,
      });
      setDownloadUrl('');
      setCustomTitle('');
      // Refresh history after a short delay to let the DB row appear.
      setTimeout(loadHistory, 1500);
    } catch (err: any) {
      const msg = err.message ?? 'Failed to start download';
      toast.error('Download failed', { description: msg });
    } finally {
      setDownloading(false);
    }
  };

  // ─── Derived ───────────────────────────────────────────────────────────────

  const qualityLabel = QUALITY_OPTIONS.find(q => q.value === settings.ufcQualityFormat)?.label ?? 'Custom';

  // ─── Render ────────────────────────────────────────────────────────────────

  if (loading) {
    return (
      <PageShell>
        <div className="flex items-center justify-center py-20">
          <ArrowPathIcon className="w-8 h-8 animate-spin text-red-500" />
        </div>
      </PageShell>
    );
  }

  return (
    <PageShell className="pb-12">
      <PageHeader
        title="UFC Fight Pass"
        subtitle="Archive UFC Fight Pass VODs via yt-dlp"
      />

      {/* ── yt-dlp Status Banner ─────────────────────────────────────────── */}
      <div className={`mb-6 flex items-center gap-3 px-4 py-3 rounded-lg border text-sm ${
        checkingBinary
          ? 'border-gray-700 bg-gray-900/60 text-gray-400'
          : ytDlpStatus?.found
          ? 'border-green-900/50 bg-green-950/30 text-green-400'
          : 'border-red-900/50 bg-red-950/30 text-red-400'
      }`}>
        {checkingBinary ? (
          <ArrowPathIcon className="w-4 h-4 animate-spin flex-shrink-0" />
        ) : ytDlpStatus?.found ? (
          <CheckCircleIcon className="w-4 h-4 flex-shrink-0" />
        ) : (
          <SignalSlashIcon className="w-4 h-4 flex-shrink-0" />
        )}
        <span>
          {checkingBinary
            ? 'Checking yt-dlp…'
            : ytDlpStatus?.found
            ? `yt-dlp ${ytDlpStatus.version} found at ${ytDlpStatus.path}`
            : 'yt-dlp not found — install it or set the path below.'}
        </span>
        <button
          onClick={checkBinary}
          disabled={checkingBinary}
          className="ml-auto text-gray-400 hover:text-white transition-colors disabled:opacity-40"
          title="Re-check"
        >
          <ArrowPathIcon className="w-4 h-4" />
        </button>
      </div>

      {/* ── Enable Toggle ────────────────────────────────────────────────── */}
      <div className="mb-6 bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-5">
        <label className="flex items-center gap-3 cursor-pointer">
          <div className="relative">
            <input
              type="checkbox"
              className="sr-only"
              checked={settings.ufcEnabled}
              onChange={(e) => setSettings(s => ({ ...s, ufcEnabled: e.target.checked }))}
            />
            <div className={`w-11 h-6 rounded-full transition-colors ${settings.ufcEnabled ? 'bg-red-600' : 'bg-gray-700'}`} />
            <div className={`absolute top-0.5 left-0.5 w-5 h-5 bg-white rounded-full shadow transition-transform ${settings.ufcEnabled ? 'translate-x-5' : ''}`} />
          </div>
          <div>
            <p className="text-white font-medium">Enable UFC Fight Pass</p>
            <p className="text-xs text-gray-400">
              Allow Sportarr to archive UFC Fight Pass VODs using yt-dlp.
            </p>
          </div>
        </label>
      </div>

      {/* ── Credentials ──────────────────────────────────────────────────── */}
      <div className="mb-6 bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-6">
        <div className="flex items-center gap-2 mb-5">
          <ShieldCheckIcon className="w-5 h-5 text-red-500" />
          <h3 className="text-lg font-semibold text-white">Credentials</h3>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
          <div>
            <Label>Email</Label>
            <Input
              type="email"
              value={settings.ufcEmail}
              onChange={(v) => setSettings(s => ({ ...s, ufcEmail: v }))}
              placeholder="you@example.com"
            />
          </div>

          <div>
            <Label>
              Password{' '}
              {settings.ufcPasswordSet && (
                <span className="text-xs text-green-500 font-normal">(saved)</span>
              )}
            </Label>
            <Input
              type="password"
              value={password}
              onChange={setPassword}
              placeholder={settings.ufcPasswordSet ? 'Leave blank to keep current' : 'Your Fight Pass password'}
            />
            {settings.ufcPasswordSet && !password && (
              <Hint>A password is already saved. Enter a new one to update it.</Hint>
            )}
          </div>
        </div>

        {/* Auth test result */}
        {authResult && (
          <div className={`mt-4 flex items-start gap-2 p-3 rounded-lg border text-sm ${
            authResult.success
              ? 'border-green-900/50 bg-green-950/30 text-green-400'
              : 'border-red-900/50 bg-red-950/30 text-red-400'
          }`}>
            {authResult.success
              ? <CheckCircleIcon className="w-4 h-4 flex-shrink-0 mt-0.5" />
              : <XCircleIcon className="w-4 h-4 flex-shrink-0 mt-0.5" />}
            <span>{authResult.message}</span>
          </div>
        )}

        <div className="mt-4 flex gap-3">
          <button
            id="ufc-test-connection-btn"
            onClick={handleTestAuth}
            disabled={testingAuth || !settings.ufcEmail}
            className="flex items-center gap-2 px-4 py-2 bg-gray-700 hover:bg-gray-600 text-white
                       rounded-lg transition-colors disabled:opacity-40 text-sm"
          >
            {testingAuth
              ? <ArrowPathIcon className="w-4 h-4 animate-spin" />
              : <ShieldCheckIcon className="w-4 h-4" />}
            {testingAuth ? 'Testing…' : 'Test Connection'}
          </button>
          <p className="self-center text-xs text-gray-500">
            Validates credentials and refreshes the session cookie.
          </p>
        </div>
      </div>

      {/* ── Download Preferences ─────────────────────────────────────────── */}
      <div className="mb-6 bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-6">
        <div className="flex items-center gap-2 mb-5">
          <FolderArrowDownIcon className="w-5 h-5 text-red-500" />
          <h3 className="text-lg font-semibold text-white">Download Preferences</h3>
        </div>

        <div className="space-y-5">
          {/* Quality */}
          <div>
            <Label>Quality</Label>
            <select
              id="ufc-quality-select"
              value={settings.ufcQualityFormat}
              onChange={(e) => setSettings(s => ({ ...s, ufcQualityFormat: e.target.value }))}
              className="w-full px-4 py-2 bg-gray-800 border border-gray-700 rounded-lg text-white
                         focus:outline-none focus:border-red-600 transition-colors"
            >
              {QUALITY_OPTIONS.map(q => (
                <option key={q.value} value={q.value}>{q.label}</option>
              ))}
            </select>
            <Hint>
              yt-dlp format: <code className="text-gray-400">{settings.ufcQualityFormat}</code>
            </Hint>
          </div>

          {/* Output Path */}
          <div>
            <Label>Output Path</Label>
            <Input
              value={settings.ufcOutputPath}
              onChange={(v) => setSettings(s => ({ ...s, ufcOutputPath: v }))}
              placeholder="/data/media/sports/UFC (default)"
            />
            <Hint>
              Root directory for UFC downloads. Schema: <code className="text-gray-400">{'/{Year}/{Event}/{Event}.mp4'}</code>
            </Hint>
          </div>

          {/* Concurrent Fragments */}
          <div>
            <div className="flex justify-between mb-1.5">
              <Label>Concurrent Fragments</Label>
              <span className="text-sm font-semibold text-red-400">{settings.ufcConcurrentFragments}</span>
            </div>
            <input
              id="ufc-concurrent-fragments-slider"
              type="range"
              min={1}
              max={10}
              value={settings.ufcConcurrentFragments}
              onChange={(e) => setSettings(s => ({ ...s, ufcConcurrentFragments: parseInt(e.target.value) }))}
              className="w-full h-2 bg-gray-700 rounded-lg appearance-none cursor-pointer accent-red-600"
            />
            <div className="flex justify-between text-xs text-gray-500 mt-1">
              <span>1 (safe)</span>
              <span>10 (fast)</span>
            </div>
            <Hint>
              Higher values download faster but may trigger rate-limiting. Default is 4.
            </Hint>
          </div>
        </div>
      </div>

      {/* ── Advanced ─────────────────────────────────────────────────────── */}
      <div className="mb-6 bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-6">
        <div className="flex items-center gap-2 mb-5">
          <Cog6ToothIcon className="w-5 h-5 text-red-500" />
          <h3 className="text-lg font-semibold text-white">Advanced</h3>
        </div>

        <div>
          <Label>yt-dlp Binary Path</Label>
          <Input
            value={settings.ufcYtDlpPath}
            onChange={(v) => setSettings(s => ({ ...s, ufcYtDlpPath: v }))}
            placeholder="/usr/local/bin/yt-dlp (auto-detected)"
          />
          <Hint>
            Override yt-dlp binary location. Leave blank to auto-discover.
          </Hint>
        </div>
      </div>

      {/* ── Save ─────────────────────────────────────────────────────────── */}
      <div className="mb-8 flex justify-end">
        <button
          id="ufc-save-settings-btn"
          onClick={handleSave}
          disabled={saving}
          className="flex items-center gap-2 px-6 py-2.5 bg-red-600 hover:bg-red-700 text-white
                     font-semibold rounded-lg transition-colors disabled:opacity-50"
        >
          {saving ? <ArrowPathIcon className="w-4 h-4 animate-spin" /> : null}
          {saving ? 'Saving…' : 'Save Settings'}
        </button>
      </div>

      {/* ── Quick Download ───────────────────────────────────────────────── */}
      <div className="mb-8 bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-6">
        <div className="flex items-center gap-2 mb-2">
          <BoltIcon className="w-5 h-5 text-yellow-400" />
          <h3 className="text-lg font-semibold text-white">Quick Download</h3>
        </div>
        <p className="text-sm text-gray-400 mb-5">
          Paste a UFC Fight Pass video URL to archive it immediately.
          The download will appear in <strong className="text-gray-300">Activity → Queue</strong>.
        </p>

        <div className="space-y-3">
          <div>
            <Label>Fight Pass URL</Label>
            <Input
              value={downloadUrl}
              onChange={setDownloadUrl}
              placeholder="https://ufcfightpass.com/video/12345"
            />
          </div>

          <div>
            <Label>Custom Title <span className="text-gray-500 font-normal">(optional)</span></Label>
            <Input
              value={customTitle}
              onChange={setCustomTitle}
              placeholder="Leave blank to fetch from Fight Pass"
            />
            <Hint>
              If left blank, the title is fetched from UFC Fight Pass via yt-dlp.
            </Hint>
          </div>

          <div className="flex justify-end pt-1">
            <button
              id="ufc-start-rip-btn"
              onClick={handleStartDownload}
              disabled={downloading || !settings.ufcEnabled || !downloadUrl.trim()}
              className="flex items-center gap-2 px-6 py-2.5 bg-yellow-500 hover:bg-yellow-400
                         text-black font-semibold rounded-lg transition-colors disabled:opacity-40"
            >
              {downloading
                ? <ArrowPathIcon className="w-4 h-4 animate-spin" />
                : <BoltIcon className="w-4 h-4" />}
              {downloading ? 'Starting…' : 'Start Rip'}
            </button>
          </div>

          {!settings.ufcEnabled && (
            <p className="text-xs text-yellow-500 text-right">
              Enable UFC Fight Pass above to unlock downloads.
            </p>
          )}
        </div>
      </div>

      {/* ── Download History ─────────────────────────────────────────────── */}
      <div className="bg-gradient-to-br from-gray-900 to-black border border-red-900/30 rounded-lg p-6">
        <div className="flex items-center justify-between mb-5">
          <h3 className="text-lg font-semibold text-white">Recent Downloads</h3>
          <button
            onClick={loadHistory}
            disabled={loadingHistory}
            className="text-gray-400 hover:text-white transition-colors disabled:opacity-40"
            title="Refresh"
          >
            <ArrowPathIcon className={`w-4 h-4 ${loadingHistory ? 'animate-spin' : ''}`} />
          </button>
        </div>

        {loadingHistory ? (
          <div className="flex justify-center py-8">
            <ArrowPathIcon className="w-6 h-6 animate-spin text-gray-500" />
          </div>
        ) : history.length === 0 ? (
          <p className="text-center text-gray-500 py-8 text-sm">
            No UFC downloads yet. Start one above!
          </p>
        ) : (
          <div className="space-y-2">
            {history.map((item) => (
              <div
                key={item.downloadId}
                className="bg-black/30 border border-gray-800 rounded-lg px-4 py-3 flex flex-col gap-1.5"
              >
                <div className="flex items-center justify-between gap-3">
                  {/* UFC badge + title */}
                  <div className="flex items-center gap-2 min-w-0">
                    <span className="flex-shrink-0 px-1.5 py-0.5 bg-red-900/40 text-red-400
                                     text-xs font-bold rounded uppercase tracking-wide">
                      UFC
                    </span>
                    <span className="text-white text-sm font-medium truncate">{item.title}</span>
                  </div>

                  {/* Status badge */}
                  <span className={`flex-shrink-0 text-xs font-medium ${STATUS_COLORS[item.status] ?? 'text-gray-400'}`}>
                    {STATUS_LABELS[item.status] ?? `Status ${item.status}`}
                  </span>
                </div>

                {/* Progress bar (show for Queued/Downloading) */}
                {(item.status === 0 || item.status === 1) && (
                  <div className="flex items-center gap-2">
                    <div className="flex-1 h-1.5 bg-gray-800 rounded-full overflow-hidden">
                      <div
                        className="h-full bg-red-600 rounded-full transition-all"
                        style={{ width: `${Math.max(0, Math.min(100, item.progress))}%` }}
                      />
                    </div>
                    <span className="text-xs text-gray-400 flex-shrink-0 w-9 text-right">
                      {item.progress.toFixed(0)}%
                    </span>
                  </div>
                )}

                {/* Error message */}
                {item.errorMessage && (
                  <p className="text-xs text-red-400 truncate">{item.errorMessage}</p>
                )}

                {/* Timestamp */}
                <p className="text-xs text-gray-600">
                  Added {new Date(item.added).toLocaleString()}
                </p>
              </div>
            ))}
          </div>
        )}
      </div>
    </PageShell>
  );
}
