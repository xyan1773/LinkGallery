# LinkGallery Frontend Contract

This document defines the frontend contract for the production LinkGallery UI.
The visual and interaction reference is:

`C:/Users/22/Desktop/linkgallery-frontend/linkgallery-frontend`

The demo project's `src/data/mock.ts` is only a visual fixture. Production UI
must not add mock media, mock albums, or synthetic source photos. All media
items come from connected or indexed devices.

## Data Ownership

LinkGallery is a read-only media manager by default.

- Device media comes from Android/device media indexes.
- Smart albums are query views over device media.
- Favorites are a local LinkGallery relationship on media IDs.
- Device albums are grouped from device media source fields.
- My Albums are local reference collections of media IDs.
- Copy/transfer creates files on the computer, but never edits source media.

## Types

```ts
export type PageId =
  | "photos"
  | "albums"
  | "album"
  | "devices"
  | "settings";

export type AlbumKind = "smart" | "device" | "custom";
export type MediaType = "photo" | "video";

export interface Album {
  id: string;
  name: string;
  kind: AlbumKind;
  itemCount: number;
  coverMediaId?: string;
  coverThumbnailUrl?: string;
  source?: string;
  smartQuery?: SmartAlbumQuery;
}

export interface MediaItem {
  id: string;
  deviceId: string;
  name: string;
  type: MediaType;
  thumbnailUrl: string;
  contentUrl?: string;
  date: string;
  capturedAt: string;
  modifiedAt: string;
  dimensions?: string;
  sizeBytes: number;
  duration?: string;
  favorite: boolean;
  sourceDevice?: string;
  sourceApplication?: string;
  relativePath?: string;
  albumIds: string[];
}

export interface Device {
  id: string;
  name: string;
  subtitle: string;
  status: "connected" | "connecting" | "offline" | "attention";
  battery?: number;
  latency?: number;
  mediaCount?: number;
}

export interface SmartAlbumQuery {
  type?: MediaType;
  favorite?: boolean;
  sourceApplication?: string;
  recentlyAddedDays?: number;
}
```

## Album Semantics

### Smart Albums

Smart albums are not stored folders. They are derived filters over the device
media index.

Required initial smart albums:

- `favorites`: media where `favorite === true`
- `videos`: media where `type === "video"`
- `photos`: media where `type === "photo"`

Optional smart albums may be added later only as query definitions, not copied
media lists.

### Favorites

Favorites are the user's own collection state in LinkGallery.

- Favorite state is stored locally by stable key: `{ deviceId, mediaId }`.
- Favoriting never modifies the phone file, EXIF, or Android MediaStore record.
- The Favorites album reads from the local favorite store plus the device media
  index.

### Device Albums

Device albums are grouped from real device metadata:

- Prefer `albumName` when the device provides a camera roll/bucket name.
- Otherwise group by `sourceDevice`, `sourceApplication`, or `relativePath`.
- Device album membership is read-only.
- Device albums cannot rename, delete, or move source files.

### My Albums

My Albums are local reference collections.

- Store only media references: `{ albumId, deviceId, mediaId }`.
- Deleting a custom album deletes only the collection relationship.
- Removing media from an album does not delete source files.

## Services

```ts
export interface NavigationService {
  navigate(page: PageId, params?: { albumId?: string }): void;
  back(): void;
}

export interface MediaService {
  listMedia(query: MediaListQuery): Promise<PagedResult<MediaItem>>;
  getMedia(mediaId: string): Promise<MediaItem>;
  getThumbnailUrl(media: Pick<MediaItem, "deviceId" | "id">, size: number): string;
  getContentUrl(media: Pick<MediaItem, "deviceId" | "id">): string;
}

export interface AlbumService {
  listAlbums(): Promise<Album[]>;
  getAlbum(albumId: string): Promise<Album>;
  createAlbum(input: CreateAlbumInput): Promise<Album>;
  renameAlbum(albumId: string, name: string): Promise<Album>;
  setAlbumCover(albumId: string, mediaId: string): Promise<Album>;
  addMedia(albumId: string, mediaIds: string[]): Promise<void>;
  removeMedia(albumId: string, mediaIds: string[]): Promise<void>;
  deleteAlbum(albumId: string): Promise<void>;
}

export interface FavoriteService {
  listFavoriteKeys(): Promise<FavoriteKey[]>;
  setFavorite(media: FavoriteKey, favorite: boolean): Promise<void>;
  setFavorites(media: FavoriteKey[], favorite: boolean): Promise<void>;
}

export interface DeviceService {
  listDevices(): Promise<Device[]>;
  connect(deviceId: string): Promise<void>;
  disconnect(deviceId: string): Promise<void>;
  reconnect(deviceId: string): Promise<void>;
}

export interface TransferService {
  copyMedia(mediaIds: string[], options?: CopyOptions): Promise<TransferTask>;
  copyAlbum(albumId: string, options?: CopyOptions): Promise<TransferTask>;
}
```

```ts
export interface MediaListQuery {
  albumId?: string;
  search?: string;
  type?: MediaType;
  favorite?: boolean;
  deviceId?: string;
  source?: string;
  cursor?: string;
  limit?: number;
  sort?: "capturedAt:desc" | "capturedAt:asc" | "name:asc" | "sizeBytes:desc";
}

export interface PagedResult<T> {
  items: T[];
  nextCursor?: string;
  hasMore: boolean;
  total?: number;
}

export interface CreateAlbumInput {
  name: string;
  coverMediaId?: string;
}

export interface FavoriteKey {
  deviceId: string;
  mediaId: string;
}

export interface CopyOptions {
  destinationDirectory?: string;
  conflictPolicy?: "keepBoth" | "replace" | "skip";
}

export interface TransferTask {
  id: string;
  status: "pending" | "copying" | "paused" | "success" | "failed" | "cancelled";
  copiedBytes: number;
  totalBytes: number;
  message: string;
  error?: string;
}
```

## Page Contracts

The production implementation should preserve the demo component boundaries:

- `PhotosPage` receives media and selection state through props.
- `AlbumsPage` receives computed album groups through props.
- `AlbumDetailPage` receives one album plus filtered media through props.
- `Sidebar` receives route state and album summaries through props.
- `MediaTile` and `AlbumCard` do not call APIs directly.

Controllers/stores own API calls, selection state, search debounce, errors, and
transfer state.

## Mapping to Current Backend

Current Android/device API already supports:

- `GET /api/v1/device`
- `GET /api/v1/media`
- `GET /api/v1/media/{id}/thumbnail`
- `GET /api/v1/media/{id}/content`

Production album/favorite state should initially live on the desktop side:

- Smart albums are computed from indexed `MediaItem` fields.
- Favorites require a local desktop favorite store.
- Custom albums require a local desktop album store.
- Device albums can be computed from `albumName`, `sourceDevice`,
  `sourceApplication`, and `relativePath`.

No phone-side mutation endpoint is required for these features.
