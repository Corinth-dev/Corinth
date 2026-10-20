/**
 * Stores verified schemas by id as their canonical JSON text. Schemas are immutable (the id is a hash of the
 * content), so entries never expire. Implement this to persist schemas; the client re-verifies what it reads.
 */
export interface CcfSchemaCache {
  /** The canonical schema text stored for `schemaId` (8 lowercase hex characters), or `undefined`. */
  get(schemaId: string): Promise<string | undefined> | string | undefined;
  /** Stores canonical schema text that was fetched and verified against its id. */
  set(schemaId: string, canonicalJson: string): Promise<void> | void;
}

/** The default cache: schemas live as long as the object does. */
export class MemorySchemaCache implements CcfSchemaCache {
  private readonly items = new Map<string, string>();

  get(schemaId: string): string | undefined {
    return this.items.get(schemaId);
  }

  set(schemaId: string, canonicalJson: string): void {
    this.items.set(schemaId, canonicalJson);
  }
}

/** Options of {@link IndexedDbSchemaCache}. */
export interface IndexedDbSchemaCacheOptions {
  /** Database name. Default `"ccf-schemas"`. */
  databaseName?: string;
  /** Object store name. Default `"schemas"`. */
  storeName?: string;
  /** An `IDBFactory` to use instead of the global `indexedDB` (for tests or workers). */
  indexedDB?: IDBFactory;
}

/** A persistent schema cache backed by IndexedDB, so a schema is fetched once per browser, not once per page load. */
export class IndexedDbSchemaCache implements CcfSchemaCache {
  private readonly databaseName: string;
  private readonly storeName: string;
  private readonly factory: IDBFactory | undefined;
  private database: Promise<IDBDatabase> | undefined;

  constructor(options: IndexedDbSchemaCacheOptions = {}) {
    this.databaseName = options.databaseName ?? "ccf-schemas";
    this.storeName = options.storeName ?? "schemas";
    this.factory = options.indexedDB ?? (globalThis as { indexedDB?: IDBFactory }).indexedDB;
  }

  async get(schemaId: string): Promise<string | undefined> {
    const db = await this.open();
    const value = await request<unknown>(db.transaction(this.storeName, "readonly").objectStore(this.storeName).get(schemaId));
    return typeof value === "string" ? value : undefined;
  }

  async set(schemaId: string, canonicalJson: string): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(this.storeName, "readwrite");
    tx.objectStore(this.storeName).put(canonicalJson, schemaId);
    await new Promise<void>((resolve, reject) => {
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  }

  /** Removes every stored schema. */
  async clear(): Promise<void> {
    const db = await this.open();
    await request(db.transaction(this.storeName, "readwrite").objectStore(this.storeName).clear());
  }

  /** Closes the database connection. */
  async close(): Promise<void> {
    if (this.database !== undefined) (await this.database).close();
    this.database = undefined;
  }

  private open(): Promise<IDBDatabase> {
    if (this.factory === undefined) return Promise.reject(new Error("IndexedDB is not available in this environment."));
    this.database ??= new Promise<IDBDatabase>((resolve, reject) => {
      const req = this.factory!.open(this.databaseName, 1);
      req.onupgradeneeded = () => {
        if (!req.result.objectStoreNames.contains(this.storeName)) req.result.createObjectStore(this.storeName);
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
    return this.database;
  }
}

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}
