# Motion card API modules

Each `MotionControl.VendorApi.*` project holds one vendor's low-level P/Invoke declarations. These projects are isolated from the framework runtime and equipment flows so that adding a vendor does not spread its SDK types into the rest of the solution.

These API modules are the native SDK boundary. A driver adapter will reference one API module and `MotionControl.Framework.Contracts`, then implement the common card and axis operations. Keep vendor-specific channel numbering, unit conversion, homing parameters, status decoding, and error codes inside that adapter. The API modules intentionally do not pretend that different vendors share identical homing, interpolation, status, or error semantics.

The first common motion contracts cover card connection, logical axes, axis status, absolute and relative moves, jog, stop, servo, homing, and logical DI/DO access. A deployment may create multiple card instances of its selected adapter. Adapter selection remains explicit in the Host; equipment flows do not reference vendor APIs.

`ZMotion/Adapter/MotionControl.Adapter.MotionCard.ZMotion` is the first concrete adapter. It is limited to the ZDevelop offline simulator at `127.0.0.1` and accepts only the reported identity `VPLC5xx-Simu`. It is not a physical-card driver. Leadshine and ADLINK currently have API declarations only.

The repository includes managed API declarations and adapter source code, but does not copy native runtime DLLs because the reference projects contain SDK versions and processor architectures that do not consistently match. See `docs/vendor-motion-card-apis.md` before configuring a deployment.
