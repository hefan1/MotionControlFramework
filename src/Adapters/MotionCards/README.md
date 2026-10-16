# Motion card API modules

Each `MotionControl.VendorApi.*` project holds one vendor's low-level P/Invoke declarations. These projects are isolated from the framework runtime and equipment flows so that adding a vendor does not spread its SDK types into the rest of the solution.

These API modules are the native SDK boundary. A driver adapter will reference one API module and `MotionControl.Framework.Contracts`, then implement the common card and axis operations. Keep vendor-specific channel numbering, unit conversion, homing parameters, status decoding, and error codes inside that adapter. The API modules intentionally do not pretend that different vendors share identical homing, interpolation, status, or error semantics.

The first common motion contracts cover card connection, logical axes, axis status, absolute and relative moves, jog, stop, servo, homing, and logical DI/DO access. A deployment may create multiple card instances of its selected adapter. Adapter selection remains explicit in the Host; equipment flows do not reference vendor APIs.

No concrete card driver is connected yet. Implement and review one driver against these contracts before treating an API declaration project as a usable adapter.

Only the managed API declaration files are included here. The native runtime DLLs are not copied into this solution because the reference projects contain SDK versions and processor architectures that do not consistently match. See `docs/vendor-motion-card-apis.md` before configuring a deployment.
