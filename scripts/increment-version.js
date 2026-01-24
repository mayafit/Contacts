/**
 * @fileoverview CalVer versioning script
 * @module scripts/increment-version
 *
 * Implements CalVer format: YYmDD-build.N
 * Where:
 * - YY = last two digits of year
 * - m = month as letter (a=Jan, b=Feb, ..., l=Dec)
 * - DD = day of month (zero-padded)
 * - N = incremental build number
 *
 * Example: 25a17-build.42 = January 17, 2025, build #42
 *
 * Usage:
 *   node scripts/increment-version.js          # Increment build number
 *   node scripts/increment-version.js --reset  # Reset build for new day
 */

const fs = require('fs');
const path = require('path');

const VERSION_FILE = path.join(__dirname, '..', 'version.json');

/**
 * Month letter mapping (a-l for Jan-Dec)
 */
const MONTH_LETTERS = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l'];

/**
 * Generates CalVer date string in YYmDD format
 * @param {Date} date - Date to format
 * @returns {string} Formatted date string
 */
function formatCalVerDate(date) {
  const year = String(date.getFullYear()).slice(-2);
  const month = MONTH_LETTERS[date.getMonth()];
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}${month}${day}`;
}

/**
 * Parses CalVer string into components
 * @param {string} version - Version string like "25a17-build.42"
 * @returns {{ dateString: string, buildNumber: number } | null}
 */
function parseCalVer(version) {
  const match = version.match(/^(\d{2}[a-l]\d{2})-build\.(\d+)$/);
  if (!match) {
    return null;
  }
  return {
    dateString: match[1],
    buildNumber: parseInt(match[2], 10),
  };
}

/**
 * Generates new CalVer version string
 * @param {string | null} currentVersion - Current version string
 * @param {boolean} forceReset - Force reset build number
 * @returns {string} New version string
 */
function generateNextVersion(currentVersion, forceReset = false) {
  const now = new Date();
  const todayDateString = formatCalVerDate(now);

  if (!currentVersion || forceReset) {
    return `${todayDateString}-build.1`;
  }

  const parsed = parseCalVer(currentVersion);

  if (!parsed) {
    // Invalid current version, start fresh
    return `${todayDateString}-build.1`;
  }

  if (parsed.dateString === todayDateString) {
    // Same day, increment build number
    return `${todayDateString}-build.${parsed.buildNumber + 1}`;
  }

  // New day, reset build number
  return `${todayDateString}-build.1`;
}

/**
 * Reads current version from version.json
 * @returns {{ version: string, timestamp: string } | null}
 */
function readVersionFile() {
  try {
    if (fs.existsSync(VERSION_FILE)) {
      const content = fs.readFileSync(VERSION_FILE, 'utf-8');
      return JSON.parse(content);
    }
  } catch (error) {
    console.error('Error reading version file:', error.message);
  }
  return null;
}

/**
 * Writes version to version.json
 * @param {string} version - Version string
 */
function writeVersionFile(version) {
  const data = {
    version,
    timestamp: new Date().toISOString(),
    format: 'CalVer: YYmDD-build.N (m=month letter a-l)',
  };

  fs.writeFileSync(VERSION_FILE, JSON.stringify(data, null, 2) + '\n');
}

/**
 * Main function
 */
function main() {
  const args = process.argv.slice(2);
  const forceReset = args.includes('--reset');

  const currentVersionData = readVersionFile();
  const currentVersion = currentVersionData?.version || null;

  const newVersion = generateNextVersion(currentVersion, forceReset);

  writeVersionFile(newVersion);

  // eslint-disable-next-line no-console
  console.log(`Version: ${currentVersion || '(none)'} -> ${newVersion}`);

  // Update package.json version field as well
  const packageJsonPath = path.join(__dirname, '..', 'package.json');
  try {
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf-8'));
    packageJson.version = newVersion;
    fs.writeFileSync(packageJsonPath, JSON.stringify(packageJson, null, 2) + '\n');
    // eslint-disable-next-line no-console
    console.log(`Updated package.json version to ${newVersion}`);
  } catch (error) {
    console.error('Error updating package.json:', error.message);
    process.exit(1);
  }
}

main();
