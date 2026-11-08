package com.mesob.core

import org.json.JSONObject

/** JNI bindings to libmesob.so (core/mesob-ffi/src/jni_api.rs). */
object MesobNative {
    init {
        System.loadLibrary("mesob")
    }

    external fun call(request: String): String
    external fun sessionRead(handle: Long, offset: Long, buf: ByteArray, off: Int, len: Int): Int
    external fun sessionSize(handle: Long): Long
    external fun sessionClose(handle: Long)
}

/** An operation the core refused, with its reason. */
class MesobException(message: String) : Exception(message)

/** One JSON request per operation; see core/mesob-ffi/src/ops.rs for the list. */
object Mesob {
    val isLab: Boolean by lazy { raw(JSONObject().put("op", "core.info")).getBoolean("lab") }

    fun call(op: String, fields: JSONObject = JSONObject()): JSONObject {
        fields.put("op", op)
        if (isLab && BuildConfig.LAB_VENDOR_PUB.isNotEmpty() && !fields.has("vendor_pub")) {
            fields.put("vendor_pub", BuildConfig.LAB_VENDOR_PUB)
        }
        return raw(fields)
    }

    private fun raw(req: JSONObject): JSONObject {
        val resp = JSONObject(MesobNative.call(req.toString()))
        if (!resp.optBoolean("ok")) throw MesobException(resp.optString("error", "unknown error"))
        return resp.getJSONObject("result")
    }

    fun obj(vararg pairs: Pair<String, Any?>): JSONObject = JSONObject().apply { pairs.forEach { (k, v) -> put(k, v) } }
}
