/**
 * @fileoverview Avatar for a contact: real photo when Google has one, else
 * initials on a stable per-contact color.
 * @module Contacts/components/common/ContactAvatar
 */

import React from 'react';
import { Avatar } from '@mui/material';
import type { Contact } from '../../types/Contact';
import { avatarColorOf, getDisplayName, getPhotoUrl, initialsOf } from '../../model/contactView';

/** Props for ContactAvatar */
export interface ContactAvatarProps {
  /** Contact to show */
  contact: Contact;
  /** Diameter in px */
  size?: number;
}

/**
 * Contact avatar.
 * @param props - Component props
 * @returns Avatar element
 */
const ContactAvatar: React.FC<ContactAvatarProps> = ({ contact, size = 32 }) => {
  const name = getDisplayName(contact);
  const photo = getPhotoUrl(contact);
  return (
    <Avatar
      src={photo}
      alt=""
      imgProps={{ referrerPolicy: 'no-referrer', loading: 'lazy' }}
      sx={{
        width: size,
        height: size,
        fontSize: size * 0.38,
        fontWeight: 600,
        bgcolor: avatarColorOf(contact.resourceName),
        color: '#fff',
        flexShrink: 0,
      }}
    >
      {initialsOf(name)}
    </Avatar>
  );
};

export default React.memo(ContactAvatar);
