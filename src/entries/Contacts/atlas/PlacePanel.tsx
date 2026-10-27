/**
 * @fileoverview A visit's place: when and how long, address and coordinates (copy, map
 * links), what is known about the place (category, website, phone, hours, details, its
 * OpenStreetMap link, visit statistics), identifying what is at the location
 * (OpenStreetMap) and associating the place with it, and editing it by hand
 * @module Contacts/atlas/PlacePanel
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  IconButton,
  LinearProgress,
  Link,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  Check,
  Close,
  ContentCopy,
  Edit,
  Language,
  Phone,
  Place,
  TravelExplore,
} from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type {
  LocationCandidate,
  LocationLookup,
  LookupAddress,
  PlaceAssociation,
  PlaceDetail,
  PlacePatch,
  VisitSummary,
} from './types';
import { VISIT_COLOR, isSet, sourceLabel } from './types';
import { formatDuration, formatWhen } from './geo';
import { formatMeters } from './fusionText';
import {
  detailRows,
  formatCoordinates,
  googleMapsUrl,
  isUnnamedPlace,
  nameFromAddress,
  openStreetMapUrl,
  osmObjectUrl,
  safeWebUrl,
  shortUrl,
  telUrl,
} from './placeText';
import PanelSection from './PanelSection';
import { tokens } from '../theme/contactsTheme';

/** Props for PlacePanel */
export interface PlacePanelProps {
  visit: VisitSummary;
  /** Back to the results */
  onClose: () => void;
  /** Shows every visit and activity at this place */
  onShowEverything: () => void;
  /** The visit's place changed (name, address, or a new place): the visit after the change */
  onChanged: (visit: VisitSummary) => void;
  /** Marks a position on the map (a hovered candidate); null removes the mark */
  onPreview: (pos: [number, number] | null) => void;
}

/** Search radii of the lookup (meters) */
const RADII = [50, 100, 250];

/** A link that opens in a new tab */
const ExternalLink: React.FC<{ href: string; children: React.ReactNode }> = ({
  href,
  children,
}) => (
  <Link href={href} target="_blank" rel="noopener noreferrer" underline="hover">
    {children}
  </Link>
);

/** Day only, e.g. "3 Mar 2025" */
const formatDay = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });

/** One label: value row of the place's facts */
const InfoRow: React.FC<{ label: string; children: React.ReactNode }> = ({ label, children }) => (
  <Box sx={{ display: 'flex', gap: 1, py: 0.25 }}>
    <Typography variant="caption" sx={{ color: tokens.textMuted, width: 84, flexShrink: 0 }}>
      {label}
    </Typography>
    <Typography
      variant="body2"
      component="div"
      dir="auto"
      sx={{ flex: 1, minWidth: 0, overflowWrap: 'anywhere' }}
    >
      {children}
    </Typography>
  </Box>
);

/** Coordinates with a copy button and links to Google Maps and OpenStreetMap */
const CoordinatesLine: React.FC<{ lat: number; lon: number }> = ({ lat, lon }) => {
  const [copied, setCopied] = useState(false);
  const text = formatCoordinates(lat, lon);

  useEffect(() => {
    if (!copied) {
      return undefined;
    }
    const t = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(t);
  }, [copied]);

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 1, mt: 0.5 }}>
      <Typography
        variant="caption"
        sx={{ color: tokens.textSecondary, fontVariantNumeric: 'tabular-nums' }}
      >
        {text}
      </Typography>
      <Tooltip title={copied ? 'Copied' : 'Copy coordinates'}>
        <IconButton
          size="small"
          aria-label="Copy coordinates"
          onClick={() => {
            navigator.clipboard
              ?.writeText(text)
              .then(() => setCopied(true))
              .catch(() => undefined);
          }}
          sx={{ p: 0.25 }}
        >
          {copied ? (
            <Check sx={{ fontSize: 14, color: tokens.accent }} />
          ) : (
            <ContentCopy sx={{ fontSize: 14 }} />
          )}
        </IconButton>
      </Tooltip>
      {copied && (
        <Typography variant="caption" sx={{ color: tokens.accent }}>
          Copied
        </Typography>
      )}
      <Typography variant="caption">
        <ExternalLink href={googleMapsUrl(lat, lon)}>Google Maps</ExternalLink>
        {' · '}
        <ExternalLink href={openStreetMapUrl(lat, lon)}>OpenStreetMap</ExternalLink>
      </Typography>
    </Box>
  );
};

/** The values of the place form */
interface PlaceFormValues {
  name: string;
  address: string;
  category: string;
  notes: string;
}

/** Name, address, category (and notes) of a place, with Save and Cancel */
const PlaceForm: React.FC<{
  initial: PlaceFormValues;
  /** Notes can be set only on an existing place */
  showNotes: boolean;
  saveLabel: string;
  busy: boolean;
  onSave: (values: PlaceFormValues) => void;
  onCancel: () => void;
}> = ({ initial, showNotes, saveLabel, busy, onSave, onCancel }) => {
  const [values, setValues] = useState(initial);
  const set = (key: keyof PlaceFormValues) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setValues((v) => ({ ...v, [key]: e.target.value }));
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.25, mt: 1.5 }}>
      <TextField
        size="small"
        label="Name"
        value={values.name}
        onChange={set('name')}
        inputProps={{ dir: 'auto' }}
        required
        autoFocus
      />
      <TextField
        size="small"
        label="Address"
        value={values.address}
        onChange={set('address')}
        inputProps={{ dir: 'auto' }}
      />
      <TextField
        size="small"
        label="Category"
        value={values.category}
        onChange={set('category')}
        inputProps={{ dir: 'auto' }}
      />
      {showNotes && (
        <TextField
          size="small"
          label="Notes"
          value={values.notes}
          onChange={set('notes')}
          inputProps={{ dir: 'auto' }}
          multiline
          minRows={2}
        />
      )}
      <Box sx={{ display: 'flex', gap: 1 }}>
        <Button
          variant="contained"
          size="small"
          disabled={busy || !values.name.trim()}
          onClick={() => onSave(values)}
        >
          {saveLabel}
        </Button>
        <Button size="small" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
      </Box>
    </Box>
  );
};

/** A named place near the point, with "This is it" */
const CandidateRow: React.FC<{
  candidate: LocationCandidate;
  /** Address to propose when the candidate has none (the looked-up one) */
  fallbackAddress: string;
  confirming: boolean;
  busy: boolean;
  onPick: () => void;
  onCancel: () => void;
  onConfirm: (values: PlaceFormValues) => void;
  onPreview: (pos: [number, number] | null) => void;
}> = ({
  candidate: c,
  fallbackAddress,
  confirming,
  busy,
  onPick,
  onCancel,
  onConfirm,
  onPreview,
}) => {
  const website = safeWebUrl(c.website);
  const tel = telUrl(c.phone);
  return (
    <Box
      onMouseEnter={() => onPreview([c.lat, c.lon])}
      onMouseLeave={() => onPreview(null)}
      onFocus={() => onPreview([c.lat, c.lon])}
      onBlur={() => onPreview(null)}
      sx={{ py: 0.75, borderBottom: `1px solid ${tokens.border}` }}
    >
      <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 0.5 }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, flexWrap: 'wrap' }}>
            <Typography variant="body2" dir="auto" sx={{ fontWeight: 600 }}>
              {c.name}
            </Typography>
            {c.localName && c.localName !== c.name && (
              <Typography variant="body2" dir="auto" sx={{ color: tokens.textSecondary }}>
                {c.localName}
              </Typography>
            )}
            {c.label && (
              <Chip
                size="small"
                label={c.label}
                variant="outlined"
                sx={{ height: 18, fontSize: 12 }}
              />
            )}
          </Box>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
            {formatMeters(c.distanceM)}
            {c.address && (
              <>
                {' · '}
                <span dir="auto">{c.address}</span>
              </>
            )}
          </Typography>
        </Box>
        {website && (
          <Tooltip title={shortUrl(website)}>
            <IconButton
              size="small"
              component="a"
              href={website}
              target="_blank"
              rel="noopener noreferrer"
              aria-label={`Website of ${c.name}`}
            >
              <Language sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        )}
        {tel && (
          <Tooltip title={c.phone ?? ''}>
            <IconButton size="small" component="a" href={tel} aria-label={`Call ${c.name}`}>
              <Phone sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        )}
        {!confirming && (
          <Button
            size="small"
            disabled={busy}
            onClick={onPick}
            aria-label={`This is ${c.name}`}
            sx={{ flexShrink: 0 }}
          >
            This is it
          </Button>
        )}
      </Box>
      {confirming && (
        <PlaceForm
          initial={{
            name: c.name,
            address: c.address ?? fallbackAddress,
            category: c.label,
            notes: '',
          }}
          showNotes={false}
          saveLabel="Save place"
          busy={busy}
          onSave={onConfirm}
          onCancel={onCancel}
        />
      )}
    </Box>
  );
};

/** The looked-up address with "Use this address" */
const AddressBlock: React.FC<{
  address: LookupAddress;
  busy: boolean;
  onUse: () => void;
}> = ({ address, busy, onUse }) => (
  <Box
    sx={{
      display: 'flex',
      alignItems: 'center',
      gap: 1,
      p: 1,
      mb: 0.5,
      borderRadius: '8px',
      bgcolor: tokens.page,
    }}
  >
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        Address
      </Typography>
      <Typography variant="body2" dir="auto">
        {address.display}
      </Typography>
    </Box>
    <Button size="small" disabled={busy} onClick={onUse} sx={{ flexShrink: 0 }}>
      Use this address
    </Button>
  </Box>
);

/**
 * A visit's place.
 * @param props - Component props
 * @returns Panel element
 */
const PlacePanel: React.FC<PlacePanelProps> = ({
  visit,
  onClose,
  onShowEverything,
  onChanged,
  onPreview,
}) => {
  const [place, setPlace] = useState<PlaceDetail | null>(null);
  const [placeError, setPlaceError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [radius, setRadius] = useState(100);
  const [lookup, setLookup] = useState<LocationLookup | null>(null);
  const [lookingUp, setLookingUp] = useState(false);
  const [lookupError, setLookupError] = useState<string | null>(null);
  // The candidate being confirmed (its ref)
  const [confirming, setConfirming] = useState<string | null>(null);
  // Only the latest lookup's answer is shown (and none after unmounting)
  const lookupSeq = useRef(0);

  // The place's details (again when the visit gets another place)
  const placeId = visit.placeId ?? null;
  useEffect(() => {
    setPlaceError(null);
    if (!placeId) {
      setPlace(null);
      return undefined;
    }
    let alive = true;
    backendApiClient
      .atlasPlace(placeId)
      .then((p) => alive && setPlace(p))
      .catch((e: Error) => {
        if (alive) {
          setPlace(null);
          setPlaceError(e.message || 'Loading the place failed');
        }
      });
    return () => {
      alive = false;
    };
  }, [placeId]);

  // The map mark goes with the panel
  useEffect(() => () => onPreview(null), [onPreview]);
  useEffect(
    () => () => {
      lookupSeq.current += 1;
    },
    [],
  );

  const identify = useCallback(
    async (r: number) => {
      lookupSeq.current += 1;
      const n = lookupSeq.current;
      setLookingUp(true);
      setLookupError(null);
      setConfirming(null);
      onPreview(null);
      try {
        const res = await backendApiClient.atlasLookup(visit.lat, visit.lon, r);
        if (n === lookupSeq.current) {
          setLookup(res);
        }
      } catch (e) {
        if (n === lookupSeq.current) {
          setLookup(null);
          setLookupError((e as Error).message || 'Looking up the location failed');
        }
      } finally {
        if (n === lookupSeq.current) {
          setLookingUp(false);
        }
      }
    },
    [visit.lat, visit.lon, onPreview],
  );

  /** Names the visit's place / associates it (creates a place when the visit has none) */
  const associate = async (body: PlaceAssociation) => {
    setBusy(true);
    setSaveError(null);
    try {
      const r = await backendApiClient.atlasAssociateVisitPlace(visit.id, body);
      setPlace(r.place);
      setConfirming(null);
      setEditing(false);
      onChanged(r.visit);
    } catch (e) {
      setSaveError((e as Error).message || 'Saving the place failed');
    } finally {
      setBusy(false);
    }
  };

  /** Changes the existing place (keeps its link) */
  const patchPlace = async (id: string, patch: PlacePatch) => {
    setBusy(true);
    setSaveError(null);
    try {
      await backendApiClient.atlasUpdatePlace(id, patch);
      const fresh = await backendApiClient.atlasPlace(id);
      setPlace(fresh);
      setEditing(false);
      onChanged({ ...visit, placeName: fresh.name, address: fresh.address });
    } catch (e) {
      setSaveError((e as Error).message || 'Saving the place failed');
    } finally {
      setBusy(false);
    }
  };

  // With a place, changes wait for its details: a save without them would replace its link
  const placeReady = !placeId || place !== null;
  const locked = busy || !placeReady;
  const name = place?.name ?? visit.placeName ?? '';
  const address = place?.address ?? visit.address ?? null;

  /** Sets the looked-up address; an unnamed place is named after its road and number */
  const applyAddress = (a: LookupAddress) => {
    const newName = isUnnamedPlace(name) ? nameFromAddress(a) : name;
    if (place) {
      void patchPlace(
        place.id,
        newName !== place.name ? { name: newName, address: a.display } : { address: a.display },
      );
    } else {
      void associate({ name: newName || a.display, address: a.display, linkedRef: null });
    }
  };

  /** Saves the manual edit */
  const saveEdit = (v: PlaceFormValues) => {
    if (place) {
      void patchPlace(place.id, {
        name: v.name.trim(),
        address: v.address.trim(),
        category: v.category.trim(),
        notes: v.notes.trim(),
      });
    } else {
      void associate({
        name: v.name.trim(),
        address: v.address.trim() || null,
        category: v.category.trim() || null,
        linkedRef: null,
      });
    }
  };

  /** Associates the place with a candidate */
  const confirmCandidate = (c: LocationCandidate, v: PlaceFormValues) =>
    void associate({
      name: v.name.trim(),
      address: v.address.trim() || null,
      category: v.category.trim() || null,
      linkedRef: c.ref,
      website: c.website,
      phone: c.phone,
      openingHours: c.openingHours,
      details: c.details,
    });

  const website = safeWebUrl(place?.website);
  const tel = telUrl(place?.phone);
  const details = detailRows(place?.details);
  const osmUrl = osmObjectUrl(place?.linkedRef);
  const showLookup = lookingUp || !!lookup || !!lookupError;

  return (
    <Box sx={{ p: 2 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <IconButton size="small" onClick={onClose} aria-label="Back to results">
          <Close fontSize="small" />
        </IconButton>
        <Place sx={{ color: VISIT_COLOR }} />
        <Typography variant="subtitle1" dir="auto" sx={{ flex: 1, minWidth: 0 }}>
          {name || 'Visit'}
        </Typography>
        <Tooltip title="Edit name and address">
          <IconButton
            size="small"
            disabled={!placeReady}
            onClick={() => setEditing((v) => !v)}
            aria-label="Edit place"
            aria-pressed={editing}
          >
            <Edit fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>
      {place?.category && (
        <Typography
          variant="caption"
          dir="auto"
          sx={{ color: tokens.textSecondary, display: 'block', ml: 5 }}
        >
          {place.category}
        </Typography>
      )}
      <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 1 }}>
        {formatWhen(visit.startTime)} ·{' '}
        {formatDuration((Date.parse(visit.endTime) - Date.parse(visit.startTime)) / 1000)}
      </Typography>
      {address && (
        <Typography variant="caption" dir="auto" sx={{ color: tokens.textMuted, display: 'block' }}>
          {address}
        </Typography>
      )}
      <CoordinatesLine lat={visit.lat} lon={visit.lon} />

      {saveError && (
        <Alert severity="error" onClose={() => setSaveError(null)} sx={{ mt: 1.5 }}>
          {saveError}
        </Alert>
      )}
      {placeError && (
        <Alert severity="warning" sx={{ mt: 1.5 }}>
          {placeError} — changes are off until it loads.
        </Alert>
      )}

      {editing && (
        <PlaceForm
          initial={{
            name: isUnnamedPlace(name) ? '' : name,
            address: address ?? '',
            category: place?.category ?? '',
            notes: place?.notes ?? '',
          }}
          showNotes={!!place}
          saveLabel="Save"
          busy={locked}
          onSave={saveEdit}
          onCancel={() => setEditing(false)}
        />
      )}

      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mt: 1.5 }}>
        <Button size="small" onClick={onShowEverything}>
          Everything at this place
        </Button>
        <Button
          size="small"
          startIcon={<TravelExplore />}
          disabled={lookingUp}
          onClick={() => void identify(radius)}
        >
          Identify this location
        </Button>
      </Box>

      {place && (
        <PanelSection
          title="Place"
          hint={`${place.visits} ${place.visits === 1 ? 'visit' : 'visits'}`}
        >
          {website && (
            <InfoRow label="Website">
              <ExternalLink href={website}>{shortUrl(website)}</ExternalLink>
            </InfoRow>
          )}
          {place.phone && (
            <InfoRow label="Phone">
              {tel ? <Link href={tel}>{place.phone}</Link> : place.phone}
            </InfoRow>
          )}
          {place.openingHours && <InfoRow label="Hours">{place.openingHours}</InfoRow>}
          {details.map((d) => (
            <InfoRow key={d.key} label={d.label}>
              {d.href ? <ExternalLink href={d.href}>{d.value}</ExternalLink> : d.value}
            </InfoRow>
          ))}
          {place.notes && (
            <InfoRow label="Notes">
              <Box component="span" sx={{ whiteSpace: 'pre-wrap' }}>
                {place.notes}
              </Box>
            </InfoRow>
          )}
          {place.linkedRef && (
            <InfoRow label="Linked to">
              {osmUrl ? <ExternalLink href={osmUrl}>OpenStreetMap</ExternalLink> : place.linkedRef}
            </InfoRow>
          )}
          <InfoRow label="Visits">
            {[
              `${place.visits} ${place.visits === 1 ? 'visit' : 'visits'}`,
              place.firstVisit ? `first ${formatDay(place.firstVisit)}` : '',
              place.lastVisit && place.lastVisit !== place.firstVisit
                ? `last ${formatDay(place.lastVisit)}`
                : '',
              place.hoursSpent > 0
                ? `${place.hoursSpent < 10 ? place.hoursSpent.toFixed(1) : Math.round(place.hoursSpent)} h`
                : '',
            ]
              .filter(Boolean)
              .join(' · ')}
          </InfoRow>
          <InfoRow label="From">{sourceLabel(place.source)}</InfoRow>
        </PanelSection>
      )}

      {showLookup && (
        <PanelSection
          title="What is here"
          extra={
            <ToggleButtonGroup
              size="small"
              exclusive
              value={radius}
              onChange={(_, v: number | null) => {
                if (isSet(v)) {
                  setRadius(v);
                  void identify(v);
                }
              }}
              aria-label="Search radius"
              sx={{ '& .MuiToggleButton-root': { py: 0, px: 0.75, fontSize: 12 } }}
            >
              {RADII.map((r) => (
                <ToggleButton key={r} value={r}>
                  {r} m
                </ToggleButton>
              ))}
            </ToggleButtonGroup>
          }
        >
          {lookingUp && (
            <Box sx={{ py: 1 }}>
              <LinearProgress />
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Looking up OpenStreetMap… usually a few seconds
              </Typography>
            </Box>
          )}
          {lookupError && !lookingUp && (
            <Typography variant="caption" component="div" sx={{ color: tokens.attention, py: 0.5 }}>
              {lookupError}
            </Typography>
          )}
          {lookup && !lookingUp && (
            <>
              {lookup.errors.map((e) => (
                <Typography
                  key={e}
                  variant="caption"
                  component="div"
                  sx={{ color: tokens.attention, py: 0.25 }}
                >
                  {e}
                </Typography>
              ))}
              {lookup.address && (
                <AddressBlock
                  address={lookup.address}
                  busy={locked}
                  onUse={() => {
                    if (lookup.address) {
                      applyAddress(lookup.address);
                    }
                  }}
                />
              )}
              {lookup.candidates.map((c) => (
                <CandidateRow
                  key={c.ref}
                  candidate={c}
                  fallbackAddress={lookup.address?.display ?? address ?? ''}
                  confirming={confirming === c.ref}
                  busy={locked}
                  onPick={() => setConfirming(c.ref)}
                  onCancel={() => setConfirming(null)}
                  onConfirm={(v) => confirmCandidate(c, v)}
                  onPreview={onPreview}
                />
              ))}
              {lookup.candidates.length === 0 && (
                <Typography variant="body2" sx={{ color: tokens.textMuted, py: 0.5 }}>
                  No named places within {radius} m.
                </Typography>
              )}
              <Typography
                variant="caption"
                component="div"
                sx={{ color: tokens.textMuted, mt: 0.75 }}
              >
                <ExternalLink href="https://www.openstreetmap.org/copyright">
                  {lookup.attribution || '© OpenStreetMap contributors'}
                </ExternalLink>
              </Typography>
            </>
          )}
        </PanelSection>
      )}
    </Box>
  );
};

export default PlacePanel;
