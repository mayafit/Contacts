/**
 * @fileoverview Call, message, WhatsApp and email buttons for a contact
 * @module Contacts/components/device/ContactActions
 */

import React, { useState } from 'react';
import { IconButton, ListItemText, Menu, MenuItem, Tooltip } from '@mui/material';
import { Call, Email, Sms, WhatsApp } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import { clean } from '../../model/contactView';
import { mailtoHref, smsHref, telHref, whatsappHref } from '../../device/device';

/** Props for ContactActions */
export interface ContactActionsProps {
  contact: Contact;
  /** Which buttons to show */
  channels?: ('call' | 'sms' | 'whatsapp' | 'email')[];
  /** Prefilled text for messages */
  message?: string;
  /** Icon size */
  size?: 'small' | 'medium';
}

type Channel = 'call' | 'sms' | 'whatsapp' | 'email';

const META: Record<Channel, { label: string; icon: React.ReactNode; color: string }> = {
  call: { label: 'Call', icon: <Call fontSize="inherit" />, color: '#4caf7d' },
  sms: { label: 'Text message', icon: <Sms fontSize="inherit" />, color: '#3987e5' },
  whatsapp: { label: 'WhatsApp', icon: <WhatsApp fontSize="inherit" />, color: '#25d366' },
  email: { label: 'Email', icon: <Email fontSize="inherit" />, color: '#e8a23a' },
};

/** Link for a channel and one value */
const hrefOf = (
  channel: Channel,
  value: string,
  canonical: string | null | undefined,
  message?: string,
) => {
  switch (channel) {
    case 'call':
      return telHref(value);
    case 'sms':
      return smsHref(value, message);
    case 'whatsapp':
      return whatsappHref(value, canonical, message);
    default:
      return mailtoHref(value, undefined, message);
  }
};

/**
 * Buttons that hand the contact to the phone's dialer, messages, WhatsApp or
 * mail app. With several numbers or addresses, a menu asks which one.
 * @param props - Component props
 * @returns Buttons
 */
const ContactActions: React.FC<ContactActionsProps> = ({
  contact,
  channels = ['call', 'sms', 'whatsapp', 'email'],
  message,
  size = 'small',
}) => {
  const [menu, setMenu] = useState<{ anchor: HTMLElement; channel: Channel } | null>(null);
  const phones = (contact.phoneNumbers ?? []).filter((p) => clean(p.value));
  const emails = (contact.emailAddresses ?? []).filter((e) => clean(e.value));

  const options = (channel: Channel) =>
    channel === 'email'
      ? emails.map((e) => ({ value: e.value, label: e.value, type: e.type, canonical: null }))
      : phones
          .map((p) => ({
            value: p.value,
            label: p.value,
            type: p.type,
            canonical: p.canonicalForm,
          }))
          .filter((o) => channel !== 'whatsapp' || !!whatsappHref(o.value, o.canonical));

  const open = (href: string) => {
    // tel:/sms:/mailto: hand over to the device; WhatsApp opens its app or the web
    window.open(href, href.startsWith('https:') ? '_blank' : '_self', 'noopener');
  };

  return (
    <>
      {channels.map((channel) => {
        const list = options(channel);
        if (!list.length) {
          return null;
        }
        const { label, icon, color } = META[channel];
        return (
          <Tooltip key={channel} title={list.length === 1 ? `${label} ${list[0].label}` : label}>
            <IconButton
              size={size}
              aria-label={`${label} ${list.length === 1 ? list[0].label : ''}`.trim()}
              sx={{ color, fontSize: size === 'small' ? 20 : 24 }}
              onClick={(e) => {
                e.stopPropagation();
                if (list.length === 1) {
                  open(hrefOf(channel, list[0].value, list[0].canonical, message));
                } else {
                  setMenu({ anchor: e.currentTarget, channel });
                }
              }}
            >
              {icon}
            </IconButton>
          </Tooltip>
        );
      })}
      <Menu
        anchorEl={menu?.anchor}
        open={!!menu}
        onClose={() => setMenu(null)}
        onClick={(e) => e.stopPropagation()}
      >
        {menu &&
          options(menu.channel).map((o) => (
            <MenuItem
              key={o.value}
              onClick={() => {
                setMenu(null);
                open(hrefOf(menu.channel, o.value, o.canonical, message));
              }}
            >
              <ListItemText primary={o.label} secondary={o.type || undefined} />
            </MenuItem>
          ))}
      </Menu>
    </>
  );
};

export default ContactActions;
