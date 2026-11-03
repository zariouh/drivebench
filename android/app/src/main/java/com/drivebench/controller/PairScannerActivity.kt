package com.drivebench.controller

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Bundle
import android.widget.*
import androidx.activity.ComponentActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import com.google.zxing.*
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** Camera is bound only to this short-lived, non-exported pairing screen. */
class PairScannerActivity : ComponentActivity() {
    private lateinit var preview: PreviewView
    private lateinit var message: TextView
    private val executor=Executors.newSingleThreadExecutor()
    private val completed=AtomicBoolean(false)
    private var provider: ProcessCameraProvider?=null
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root=LinearLayout(this).apply { orientation=LinearLayout.VERTICAL; setBackgroundColor(Color.BLACK); setPadding(24,24,24,24) }
        message=TextView(this).apply { text="Point at the pairing QR in the DriveBench PC window"; setTextColor(Color.WHITE); textSize=17f }
        root.addView(message)
        preview=PreviewView(this); root.addView(preview,LinearLayout.LayoutParams(-1,0,1f))
        root.addView(Button(this).apply { text="Cancel"; setOnClickListener { finish() } })
        setContentView(root)
        if(checkSelfPermission(Manifest.permission.CAMERA)==PackageManager.PERMISSION_GRANTED) startCamera()
        else requestPermissions(arrayOf(Manifest.permission.CAMERA),72)
    }
    @Deprecated("Native permission bridge")
    override fun onRequestPermissionsResult(requestCode: Int,permissions: Array<String>,grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode,permissions,grantResults)
        if(requestCode==72 && grantResults.firstOrNull()==PackageManager.PERMISSION_GRANTED) startCamera()
        else message.text="Camera permission was not granted. Cancel and use Paste pairing text instead."
    }
    private fun startCamera() {
        val future=ProcessCameraProvider.getInstance(this)
        future.addListener({
            try {
                if(isFinishing || isDestroyed) return@addListener
                provider=future.get()
                val cameraPreview=Preview.Builder().build().also { it.setSurfaceProvider(preview.surfaceProvider) }
                val analysis=ImageAnalysis.Builder().setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
                val reader=MultiFormatReader().apply { setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),DecodeHintType.TRY_HARDER to true)) }
                analysis.setAnalyzer(executor) { image ->
                    try {
                        if(completed.get()) return@setAnalyzer
                        val plane=image.planes[0]; val width=image.width; val height=image.height
                        val source=plane.buffer.duplicate();val data=ByteArray(width*height)
                        for(y in 0 until height) for(x in 0 until width) data[y*width+x]=source.get(y*plane.rowStride+x*plane.pixelStride)
                        val luminance=PlanarYUVLuminanceSource(data,width,height,0,0,width,height,false)
                        val result=reader.decodeWithState(BinaryBitmap(HybridBinarizer(luminance)))
                        PairInfo.parse(result.text)
                        if(completed.compareAndSet(false,true)) runOnUiThread {
                            setResult(RESULT_OK,Intent().putExtra("pairing",result.text));finish()
                        }
                    } catch (_: Exception) { /* Other QR codes and undecodable frames are ignored. */ }
                    finally { reader.reset(); image.close() }
                }
                provider!!.unbindAll(); provider!!.bindToLifecycle(this,CameraSelector.DEFAULT_BACK_CAMERA,cameraPreview,analysis)
            } catch (_: Exception) { message.text="Camera unavailable. Cancel and use Paste pairing text." }
        }, java.util.concurrent.Executor { action -> runOnUiThread(action) })
    }
    override fun onDestroy() { provider?.unbindAll();executor.shutdownNow();super.onDestroy() }
}
