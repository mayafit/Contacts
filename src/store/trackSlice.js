import { createSlice } from '@reduxjs/toolkit';

const trackDefaultState = {
  lon: 0,
  lat: 0,
  alt: 0,
  speed: 0,
};

const trackSlice = createSlice({
  name: 'track',
  initialState: trackDefaultState,
  reducers: {
    updateTrack(state, action) {
      //   const { lon, lat, alt, speed } = action.payload;
      const newState = action.payload;
      setImmutableObject(state, newState);
    },
  },
});

export const { updateTrack: updateTrackAction } = trackSlice.actions;

export default trackSlice.reducer;
